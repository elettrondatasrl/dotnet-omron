using System;

namespace RICADO.Omron
{
    /// <summary>
    /// The request was not sent: the PLC is down and the next attempt is not due yet (see
    /// <see cref="ReconnectPolicy"/>). Nothing reached the network, so retrying it later is safe,
    /// writes included.
    /// </summary>
    public sealed class PlcLinkDownException : OmronException
    {
        internal PlcLinkDownException(string host, int port, TimeSpan? downFor, TimeSpan? nextAttemptIn)
            : base("Omron PLC '" + host + ":" + port + "' is not answering"
                   + (downFor is TimeSpan down ? " for " + (int)down.TotalSeconds + "s" : "")
                   + " - Request not sent"
                   + (nextAttemptIn is TimeSpan next ? ", next attempt in " + (int)Math.Ceiling(next.TotalSeconds) + "s" : ""))
        {
            DownFor = downFor;
            NextAttemptIn = nextAttemptIn;
        }

        /// <summary>How long the PLC has not answered.</summary>
        public TimeSpan? DownFor { get; }

        /// <summary>How long until the next attempt.</summary>
        public TimeSpan? NextAttemptIn { get; }
    }
}
