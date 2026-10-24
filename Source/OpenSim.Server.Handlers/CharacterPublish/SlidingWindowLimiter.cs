/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
namespace OpenSim.Server.Handlers.CharacterPublish;

/// <summary>
/// Counts hits per key inside a sliding window: a key may hit <c>limit</c> times in any <c>window</c>. The clock is a parameter so a
/// test can move it. In memory and per process (two Robust processes each keep their own count).
/// </summary>
public sealed class SlidingWindowLimiter
{
    const int MaxKeys = 10000;
    readonly Func<DateTime> _now;
    readonly Dictionary<string, Queue<DateTime>> _hits = new();
    readonly object _lock = new();

    public SlidingWindowLimiter(Func<DateTime> now) => _now = now;

    /// <summary>Records a hit unless the key is over its limit; when it is, says how long until a hit would be allowed again.</summary>
    public bool TryHit(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        lock (_lock)
        {
            var now = _now();
            if (_hits.Count >= MaxKeys) Prune(now, window);
            if (!_hits.TryGetValue(key, out var q)) _hits[key] = q = new Queue<DateTime>();
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= limit)
            {
                retryAfter = window - (now - q.Peek());
                if (retryAfter < TimeSpan.FromSeconds(1)) retryAfter = TimeSpan.FromSeconds(1);
                return false;
            }
            q.Enqueue(now);
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    void Prune(DateTime now, TimeSpan window)
    {
        foreach (var k in _hits.Where(p => p.Value.Count == 0 || now - p.Value.Last() >= window).Select(p => p.Key).ToList())
            _hits.Remove(k);
    }
}
