using System;
using System.Collections.Generic;
using System.Linq;

namespace RICADO.Omron
{
    /// <summary>
    /// What to do while the PLC does not answer. Off by default (<see cref="OmronPLC.ReconnectPolicy"/>
    /// null): every request goes to the network, as it always did.
    ///
    /// When set, a request that gets no answer marks the PLC as down. From then on requests fail at
    /// once with <see cref="PlcLinkDownException"/>, without touching the network, until the next
    /// attempt is due: after 1, 2, 3 seconds, then every 5 (<see cref="Delays"/>). Only one caller gets
    /// each attempt. When the ping has been seen working with this PLC, the attempt is made only if
    /// the PLC answers the ping: a PLC that is off costs no connection attempt and no exception chain.
    /// The first answer from the PLC brings it back up (<see cref="OmronPLC.LinkStateChanged"/>).
    /// </summary>
    public sealed class ReconnectPolicy
    {
        private static readonly TimeSpan[] DefaultDelays =
        {
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
        };

        /// <summary>1, 2, 3 seconds, then every 5; ping on, 500 ms.</summary>
        public static ReconnectPolicy Default => new ReconnectPolicy();

        /// <summary>
        /// Wait before each attempt while the PLC is down; the last one repeats. Not empty, no
        /// negative values.
        /// </summary>
        public IReadOnlyList<TimeSpan> Delays { get; init; } = DefaultDelays;

        /// <summary>
        /// Ping the PLC before each attempt. It counts only once the PLC has answered a ping while
        /// up: with ICMP blocked by a firewall a silent ping says nothing, and only the delays apply.
        /// </summary>
        public bool UsePing { get; init; } = true;

        /// <summary>Time limit of each ping, in milliseconds.</summary>
        public int PingTimeoutMs { get; init; } = 500;

        internal void Validate()
        {
            if (Delays == null || Delays.Count == 0)
            {
                throw new ArgumentException("The Reconnect Policy needs at least one Delay", nameof(Delays));
            }

            if (Delays.Any(d => d < TimeSpan.Zero))
            {
                throw new ArgumentException("The Reconnect Policy Delays cannot be Negative", nameof(Delays));
            }

            if (PingTimeoutMs <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(PingTimeoutMs), "The Ping Timeout must be greater than Zero");
            }
        }
    }
}
