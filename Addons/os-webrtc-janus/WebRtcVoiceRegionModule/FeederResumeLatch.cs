using System;
using System.Threading;

namespace osWebRtcVoice
{
    /// <summary>Slice V-1b (O-120): the one-way gate between "the Watchdog noticed this feeder stalled" and "the
    /// tick that finally runs rebuilds from the scene".
    ///
    /// It exists as its own type because the two halves run on different threads and neither can be tested through
    /// <see cref="VoiceVisibilityService"/> without a whole Scene. The failure this pins is not "does Invalidate
    /// rebuild" (VoiceStateFeederTests covers that) but the wiring around it: an alarm that fires, sets a flag, and
    /// then has nothing consume it, leaving the next tick to derive from the cached pre-stall view.
    /// </summary>
    /// <remarks>Public only so the tests can reach it; it is an implementation detail of the feeder loop.</remarks>
    public sealed class FeederResumeLatch
    {
        private int _tripped;

        /// <summary>The Watchdog's alarm route. Does no work and takes no lock deliberately: it runs on the
        /// Watchdog's own thread, which O-120 measured being starved too.</summary>
        public void Trip() => Interlocked.Exchange(ref _tripped, 1);

        /// <summary>Test observability only; the tick uses <see cref="ResumeIfStalled"/>.</summary>
        public bool IsTripped => Volatile.Read(ref _tripped) != 0;

        /// <summary>The tick's route. If the latch was tripped, clears it and calls <paramref name="invalidate"/>
        /// so the derivation that follows is rebuilt from the world rather than replayed from cache (ruling 4).
        /// Returns true when it did, so the caller can log the resume. Clearing before invalidating means a stall
        /// that begins again during the rebuild trips the latch afresh rather than being swallowed.</summary>
        public bool ResumeIfStalled(Action invalidate)
        {
            if (Interlocked.Exchange(ref _tripped, 0) == 0)
                return false;
            invalidate?.Invoke();
            return true;
        }
    }
}
