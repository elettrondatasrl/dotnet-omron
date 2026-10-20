using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace RICADO.Omron.Channels
{
    /// <summary>
    /// Stands in front of the channel while the PLC does not answer (see <see cref="ReconnectPolicy"/>):
    /// decides when an attempt is due, pings before it when the ping is known to work, and tells
    /// when the PLC goes down and comes back. Thread-safe.
    /// </summary>
    internal sealed class LinkGuard
    {
        private readonly string _host;
        private readonly ReconnectPolicy _policy;
        private readonly Func<DateTime> _clock;
        private readonly Func<string, int, Task<bool?>> _ping;
        private readonly ReconnectBackoff _backoff;
        private readonly object _sync = new object();
        private bool? _pingWorks;
        private int _learning;

        /// <param name="ping">Replaceable in tests; returns null when the ping could not be sent.</param>
        internal LinkGuard(string host, ReconnectPolicy policy, Func<DateTime> clock = null, Func<string, int, Task<bool?>> ping = null)
        {
            policy.Validate();
            _host = host;
            _policy = policy;
            _clock = clock ?? (() => DateTime.UtcNow);
            _ping = ping ?? PingAsync;
            // A copy: a list changed by the caller afterwards must not change the rules halfway.
            _backoff = new ReconnectBackoff(policy.Delays.ToArray());
        }

        /// <summary>Raised outside any lock: down (with the cause) or back up (with how long it was down).</summary>
        internal event EventHandler<LinkStateChangedEventArgs> StateChanged;

        internal bool IsDown { get { lock (_sync) return _backoff.IsDown; } }

        internal TimeSpan? DownFor
        {
            get { lock (_sync) return _backoff.DownSince is DateTime since ? _clock() - since : null; }
        }

        /// <summary>How long until the next attempt; null while up.</summary>
        internal TimeSpan? NextAttemptIn
        {
            get
            {
                lock (_sync)
                {
                    if (!_backoff.IsDown) return null;
                    var wait = _backoff.NextAttempt - _clock();
                    return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
                }
            }
        }

        /// <summary>
        /// Is an attempt worth making now? Always while up. While down only when it is due, and only
        /// if the PLC answers the ping (when the ping counts). Never throws: a silent ping counts as a
        /// failed attempt.
        /// </summary>
        internal async Task<bool> ShouldTryAsync(CancellationToken cancellationToken)
        {
            bool usePing;
            lock (_sync)
            {
                if (!_backoff.IsDown) return true;
                if (!_backoff.TryReserve(_clock())) return false;
                usePing = _policy.UsePing && _pingWorks == true;
            }

            if (cancellationToken.IsCancellationRequested) return false;
            if (!usePing) return true;

            if (await _ping(_host, _policy.PingTimeoutMs).ConfigureAwait(false) == true) return true;
            lock (_sync) _backoff.Failed(_clock());
            return false;
        }

        /// <summary>The PLC did not answer.</summary>
        internal void ReportFault(Exception cause)
        {
            bool first;
            lock (_sync) first = _backoff.Failed(_clock());
            if (first)
            {
                Raise(new LinkStateChangedEventArgs(true, cause, null));
            }
        }

        /// <summary>The PLC answered.</summary>
        internal void ReportOk()
        {
            TimeSpan? wasDown;
            bool learn;
            lock (_sync)
            {
                wasDown = _backoff.Succeeded(_clock());
                learn = _policy.UsePing && _pingWorks == null;
            }

            if (learn && Interlocked.Exchange(ref _learning, 1) == 0)
            {
                _ = LearnPingAsync();
            }

            if (wasDown is TimeSpan down)
            {
                Raise(new LinkStateChangedEventArgs(false, null, down));
            }
        }

        /// <summary>With the PLC answering: does it answer the ping too? From then on a silent ping means down.</summary>
        private async Task LearnPingAsync()
        {
            try
            {
                var works = await _ping(_host, _policy.PingTimeoutMs).ConfigureAwait(false);
                lock (_sync) _pingWorks = works == true;
            }
            catch
            {
                lock (_sync) _pingWorks = false;
            }
        }

        private void Raise(LinkStateChangedEventArgs args)
        {
            try
            {
                StateChanged?.Invoke(this, args);
            }
            catch
            {
                // A failing handler (a logger, a UI update) must not break the communication.
            }
        }

        private static async Task<bool?> PingAsync(string host, int timeoutMs)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(host, timeoutMs).ConfigureAwait(false);
                return reply.Status == IPStatus.Success;
            }
            catch (PingException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (ArgumentException) { return null; }
        }
    }
}
