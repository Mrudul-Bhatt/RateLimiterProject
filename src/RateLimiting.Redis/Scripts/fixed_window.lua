-- ============================================================================================
-- FIXED-WINDOW COUNTER — runs atomically on the Redis server.
-- ============================================================================================
--
-- HOW LUA-IN-REDIS WORKS (read this first if Lua is new to you):
--
--  * Redis runs this whole script single-threaded and atomically. Nothing else touches these keys
--    until the script finishes. That is what removes the race between app instances.
--  * The app passes data in through two predefined arrays:
--        KEYS[i] = the Redis keys the script operates on   (here: KEYS[1] = the rate-limit key)
--        ARGV[i] = plain arguments (numbers/strings)        (here: the limit and the window)
--    IMPORTANT: Lua arrays are 1-BASED, so the first element is [1], not [0].
--  * `redis.call('CMD', ...)` runs a normal Redis command (GET, INCR, ...) from inside the script.
--  * Everything arrives as a STRING, even numbers. `tonumber(x)` converts a string to a number.
--  * `local x = ...` declares a local variable (always use `local`; globals are disallowed here).
--  * `--` starts a comment (like `//` in C#). There is no `//`.
--  * We return a Lua TABLE `{ a, b, c }` (an array). StackExchange.Redis receives it as an array
--    of RedisResult, which the C# side reads as raw[0], raw[1], raw[2].
--
-- Inputs:
--   KEYS[1] = the rate-limit key            e.g. "rl:fixed:ip:1.2.3.4"
--   ARGV[1] = limit          (max requests allowed per window)
--   ARGV[2] = windowMillis   (window length in ms; also used as the key's TTL)
--
-- Output (a 3-element array):
--   { allowed, remaining, ttlMillis }   where allowed is 1 (yes) or 0 (no)
-- ============================================================================================

-- Convert the two string arguments into numbers we can do maths with.
local limit = tonumber(ARGV[1])
local windowMillis = tonumber(ARGV[2])

-- Read the current counter for this key.
--   redis.call('GET', KEYS[1]) returns the stored value, or the Lua value `false` if the key
--   doesn't exist yet. `X or '0'` means "use X, but if X is false/nil use '0' instead" — a common
--   Lua idiom for defaults. So a missing key is treated as "0".
local current = tonumber(redis.call('GET', KEYS[1]) or '0')

-- Would admitting this request push us OVER the limit? If so, reject WITHOUT incrementing.
-- (Checking before incrementing keeps the counter from running away past the limit under overload,
-- and keeps `remaining` from going negative.)
if current + 1 > limit then
    -- PTTL returns the key's remaining time-to-live in milliseconds.
    -- It returns -2 if the key doesn't exist and -1 if it exists but has no expiry; guard for those.
    local ttl = redis.call('PTTL', KEYS[1])
    if ttl < 0 then
        ttl = windowMillis
    end
    -- allowed = 0 (blocked), remaining = 0, and how long until the window resets.
    return { 0, 0, ttl }
end

-- We're under the limit, so admit the request: INCR atomically adds 1 and returns the NEW value.
-- If the key didn't exist, Redis creates it starting at 0 then increments, so `current` becomes 1.
current = redis.call('INCR', KEYS[1])

-- Only the FIRST request in a window sets the TTL. That makes it a true fixed window: the entire
-- window shares one expiry instant, rather than every request pushing the expiry further out.
-- (`==` is equality in Lua, same as C#. Not-equal is `~=`, which is the one to remember.)
if current == 1 then
    redis.call('PEXPIRE', KEYS[1], windowMillis)   -- PEXPIRE sets a TTL in milliseconds
end

-- Read the TTL back so the caller can report an accurate reset time (again guarding -1/-2).
local ttl = redis.call('PTTL', KEYS[1])
if ttl < 0 then
    ttl = windowMillis
end

-- allowed = 1, remaining = how many are left, ttl = ms until the window resets.
return { 1, limit - current, ttl }
