-- ============================================================================================
-- COST-BASED WINDOW — atomic "debit N units from a counter, if it fits" on the Redis server.
-- ============================================================================================
-- This is the fixed-window counter extended to CHARGE A VARIABLE COST per request (Level 6). A
-- basic call costs 1, an image call costs 10; the window admits the request only if the whole cost
-- fits under the limit. Because it's one Lua script, the check-and-debit is atomic across instances.
-- (If Lua is new to you, read fixed_window.lua's header first — same rules apply.)
--
-- Inputs:
--   KEYS[1] = counter key            e.g. "q:min:alice:202607032015"
--   ARGV[1] = limit          (max units allowed in the window)
--   ARGV[2] = cost           (units this request wants to debit)
--   ARGV[3] = ttlMillis      (window length in ms; set as the key TTL on first write)
--
-- Output: { allowed, remaining, ttlMillis }   allowed is 1 (fit) or 0 (would overflow)
-- ============================================================================================

local limit = tonumber(ARGV[1])
local cost = tonumber(ARGV[2])
local ttl = tonumber(ARGV[3])

local current = tonumber(redis.call('GET', KEYS[1]) or '0')

-- Would this request's cost push us over the limit? Reject WITHOUT debiting.
if current + cost > limit then
    local pttl = redis.call('PTTL', KEYS[1])
    if pttl < 0 then pttl = ttl end
    return { 0, limit - current, pttl }
end

-- Fits: INCRBY adds `cost` atomically (not just 1) and returns the new total.
current = redis.call('INCRBY', KEYS[1], cost)

-- First write into a fresh window starts the TTL. (current == cost means it was 0 before.)
if current == cost then
    redis.call('PEXPIRE', KEYS[1], ttl)
end

local pttl = redis.call('PTTL', KEYS[1])
if pttl < 0 then pttl = ttl end
return { 1, limit - current, pttl }
