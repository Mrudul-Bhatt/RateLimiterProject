-- ============================================================================================
-- SLIDING-WINDOW LOG — runs atomically on the Redis server, backed by a SORTED SET.
-- ============================================================================================
--
-- If you haven't read fixed_window.lua yet, read its header first — it explains the Lua basics
-- (KEYS/ARGV arrays are 1-based, redis.call, tonumber, `local`, returning a table, etc.).
--
-- THE DATA STRUCTURE — a Redis SORTED SET:
--   A sorted set stores unique "members", each with a numeric "score", kept ordered by score.
--   Here: one member per request, and the score is the request's timestamp (in ms). So the set is
--   literally Level 2's per-key timestamp log, but stored in Redis and shared by all instances.
--   Relevant commands:
--     ZREMRANGEBYSCORE key min max  -> delete members whose score is in [min, max]  (evict old)
--     ZCARD key                     -> how many members are in the set               (count)
--     ZADD key score member         -> add a member with a score                     (record)
--     ZRANGE key 0 0 WITHSCORES     -> the lowest-scored member (the oldest) + its score
--
-- Inputs:
--   KEYS[1] = the rate-limit key      e.g. "rl:sliding:ip:1.2.3.4"
--   ARGV[1] = nowMillis      (current time in ms — passed in from the app clock)
--   ARGV[2] = windowMillis   (length of the trailing window in ms)
--   ARGV[3] = limit          (max requests allowed within the window)
--   ARGV[4] = member         (a UNIQUE id for this request, so two requests with the same
--                             timestamp don't collide — the app sends "<now>-<guid>")
--
-- Output (a 3-element array):
--   { allowed, remaining, retryAfterMillis }   allowed is 1 (yes) or 0 (no)
-- ============================================================================================

-- Convert string args to numbers (member stays a string — it's just an id).
local nowMillis = tonumber(ARGV[1])
local windowMillis = tonumber(ARGV[2])
local limit = tonumber(ARGV[3])
local member = ARGV[4]

-- 1) EVICT everything older than the trailing window.
--    The window covers (now - windowMillis, now]. Anything with a score at or below
--    (now - windowMillis) has aged out, so delete scores in the range [0 .. now-windowMillis].
redis.call('ZREMRANGEBYSCORE', KEYS[1], 0, nowMillis - windowMillis)

-- 2) COUNT what's still inside the window after eviction.
local count = redis.call('ZCARD', KEYS[1])

-- 3a) Under the limit? Then admit this request.
if count < limit then
    -- Record it: add this request as a member scored by the current time.
    redis.call('ZADD', KEYS[1], nowMillis, member)
    -- (Re)set a TTL so a key that stops receiving traffic disappears on its own — this is why the
    -- Redis version needs NO background cleanup job (unlike Level 2's in-memory sliding log).
    redis.call('PEXPIRE', KEYS[1], windowMillis)
    -- allowed = 1; remaining = limit - count - 1 (the -1 accounts for the one we just added);
    -- retryAfter = 0 because we didn't block.
    return { 1, limit - count - 1, 0 }
end

-- 3b) Full: reject, and compute how long until a slot frees.
--     A slot opens when the OLDEST in-window request slides out, i.e. at (oldestScore + window).
--     ZRANGE ... 0 0 WITHSCORES returns a flat table { member, score }; in Lua that's
--     { oldest[1] = member, oldest[2] = score }.  Remember: 1-based indexing.
local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
local retryAfter = 0
-- `if oldest[2] then` is true only when a score exists (the set is non-empty). A nil/false value
-- is treated as "false" in an `if`, so this safely handles the empty case.
if oldest[2] then
    retryAfter = (tonumber(oldest[2]) + windowMillis) - nowMillis
    if retryAfter < 0 then
        retryAfter = 0
    end
end

-- allowed = 0 (blocked), remaining = 0, retryAfter = ms until the oldest entry expires.
return { 0, 0, retryAfter }
