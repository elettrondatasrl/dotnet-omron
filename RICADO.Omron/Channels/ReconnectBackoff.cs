using System;
using System.Collections.Generic;

namespace RICADO.Omron.Channels
{
    /// <summary>
    /// When to try again with a PLC that does not answer. Pure rules, with the time given from
    /// outside so they can be tested offline. Not thread-safe: <see cref="LinkGuard"/> protects it.
    /// </summary>
    internal sealed class ReconnectBackoff
    {
        private readonly IReadOnlyList<TimeSpan> _steps;
        private int _failures;
        private DateTime _nextAttempt;

        internal ReconnectBackoff(IReadOnlyList<TimeSpan> steps)
        {
            _steps = steps;
        }

        /// <summary>Since when the PLC does not answer; null while it answers.</summary>
        internal DateTime? DownSince { get; private set; }

        internal bool IsDown => DownSince != null;

        /// <summary>When the next attempt is due; meaningful only while down.</summary>
        internal DateTime NextAttempt => _nextAttempt;

        /// <summary>
        /// An attempt can be made now: the PLC answers, or the wait is over. While down the attempt
        /// is booked: whoever asks right after waits, so several callers do not try together.
        /// </summary>
        internal bool TryReserve(DateTime now)
        {
            if (!IsDown) return true;
            if (now < _nextAttempt) return false;
            _nextAttempt = now + Step;
            return true;
        }

        private TimeSpan Step => _steps[Math.Min(_failures, _steps.Count - 1)];

        /// <summary>An attempt failed: the next one moves further. True if the PLC was answering until now.</summary>
        internal bool Failed(DateTime now)
        {
            bool first = !IsDown;
            DownSince ??= now;
            _nextAttempt = now + Step;
            _failures++;
            return first;
        }

        /// <summary>The PLC answered. How long it had not answered, or null if it was answering.</summary>
        internal TimeSpan? Succeeded(DateTime now)
        {
            if (DownSince is not DateTime since) return null;
            DownSince = null;
            _failures = 0;
            return now - since;
        }
    }
}
