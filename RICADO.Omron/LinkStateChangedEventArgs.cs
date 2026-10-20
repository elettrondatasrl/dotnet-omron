using System;

namespace RICADO.Omron
{
    /// <summary>The PLC stopped answering, or answers again (see <see cref="ReconnectPolicy"/>).</summary>
    public sealed class LinkStateChangedEventArgs : EventArgs
    {
        internal LinkStateChangedEventArgs(bool isDown, Exception cause, TimeSpan? downFor)
        {
            IsDown = isDown;
            Cause = cause;
            DownFor = downFor;
        }

        /// <summary>True: the PLC stopped answering. False: it answers again.</summary>
        public bool IsDown { get; }

        /// <summary>The fault that brought the PLC down; null when it comes back.</summary>
        public Exception Cause { get; }

        /// <summary>How long the PLC did not answer; null when it goes down.</summary>
        public TimeSpan? DownFor { get; }
    }
}
