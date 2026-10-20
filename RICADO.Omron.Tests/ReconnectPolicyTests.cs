using System.Diagnostics;
using RICADO.Omron.Channels;

namespace RICADO.Omron.Tests
{
    /// <summary>
    /// The reconnect policy (opt-in): while the PLC is down requests fail at once without touching
    /// the network, one attempt per turn, and with a working ping a silent PLC costs no attempt.
    /// </summary>
    public class ReconnectPolicyTests
    {
        private const int Timeout = 300;

        private sealed class Rig : IDisposable
        {
            public readonly FakeFinsServer Server = new FakeFinsServer { [100] = 7 };
            public DateTime Now = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
            public bool? PingAnswer = true;
            public int Pings;
            public readonly List<LinkStateChangedEventArgs> Events = new List<LinkStateChangedEventArgs>();
            public OmronPLC Plc;

            public static async Task<Rig> StartAsync(bool withPolicy = true, bool initialize = true)
            {
                var rig = new Rig();
                rig.Plc = new OmronPLC(50, 10, "127.0.0.1", rig.Server.Port, Timeout, 1);
                rig.Plc.LinkStateChanged += (_, e) => { lock (rig.Events) rig.Events.Add(e); };
                if (withPolicy)
                    rig.Plc.SetReconnectPolicy(ReconnectPolicy.Default, () => rig.Now,
                        (_, _) => { Interlocked.Increment(ref rig.Pings); return Task.FromResult(rig.PingAnswer); });
                if (initialize)
                    await rig.Plc.InitializeAsync(TestContext.Current.CancellationToken);
                return rig;
            }

            public async Task<ushort> ReadAsync(ushort address = 100)
                => (await Plc.ReadWordAsync(address, MemoryWordDataType.DataMemory, TestContext.Current.CancellationToken)).Values[0];

            /// <summary>The PLC goes away and a request finds out.</summary>
            public async Task GoDownAsync()
            {
                Server.Stop();
                var ex = await Assert.ThrowsAsync<OmronException>(() => ReadAsync());
                Assert.IsNotType<PlcLinkDownException>(ex);
                Assert.True(Plc.IsLinkDown);
            }

            public void Dispose()
            {
                Plc?.Dispose();
                Server.Dispose();
            }
        }

        [Fact]
        public async Task WithoutAPolicyEveryRequestGoesToTheNetwork()
        {
            using var rig = await Rig.StartAsync(withPolicy: false);
            rig.Server.Stop();

            for (int i = 0; i < 2; i++)
            {
                var ex = await Assert.ThrowsAsync<OmronException>(() => rig.ReadAsync());
                Assert.IsNotType<PlcLinkDownException>(ex);
            }
            Assert.False(rig.Plc.IsLinkDown);
            Assert.Empty(rig.Events);
        }

        [Fact]
        public async Task WhileDownARequestFailsAtOnceWithoutTouchingTheNetwork()
        {
            using var rig = await Rig.StartAsync();
            await rig.GoDownAsync();

            var down = Assert.Single(rig.Events);
            Assert.True(down.IsDown);
            Assert.NotNull(down.Cause);

            rig.Server.Start();
            int accepted = rig.Server.Accepted;
            var clock = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<PlcLinkDownException>(() => rig.ReadAsync());

            Assert.True(clock.ElapsedMilliseconds < Timeout, $"took {clock.ElapsedMilliseconds} ms");
            Assert.Equal(accepted, rig.Server.Accepted);
            Assert.Equal(TimeSpan.FromSeconds(1), ex.NextAttemptIn);
        }

        [Fact]
        public async Task AtItsTurnTheRequestGoesAndThePlcComesBack()
        {
            using var rig = await Rig.StartAsync();
            await rig.GoDownAsync();
            rig.Server.Start();

            rig.Now += TimeSpan.FromSeconds(1);
            Assert.Equal(7, await rig.ReadAsync());

            Assert.False(rig.Plc.IsLinkDown);
            Assert.Equal(2, rig.Events.Count);
            Assert.False(rig.Events[1].IsDown);
            Assert.Equal(TimeSpan.FromSeconds(1), rig.Events[1].DownFor);
        }

        [Fact]
        public async Task ManyCallersAtTheSameTurnOpenOneConnection()
        {
            using var rig = await Rig.StartAsync();
            await rig.GoDownAsync();
            rig.Server.Start();
            int accepted = rig.Server.Accepted;

            rig.Now += TimeSpan.FromSeconds(1);
            var reads = Enumerable.Range(0, 6).Select(_ => rig.ReadAsync()).ToArray();
            try { await Task.WhenAll(reads); } catch { /* checked one by one */ }

            Assert.Contains(reads, r => r.IsCompletedSuccessfully);
            Assert.All(reads.Where(r => r.IsFaulted), r => Assert.IsType<PlcLinkDownException>(r.Exception.InnerException));
            Assert.Equal(accepted + 1, rig.Server.Accepted);
        }

        [Fact]
        public async Task WithAWorkingPingASilentPlcCostsNoAttempt()
        {
            using var rig = await Rig.StartAsync();
            Assert.Equal(1, rig.Pings);   // learnt with the PLC up: the ping works

            await rig.GoDownAsync();
            rig.Server.Start();
            int accepted = rig.Server.Accepted;

            rig.PingAnswer = false;
            rig.Now += TimeSpan.FromSeconds(1);
            await Assert.ThrowsAsync<PlcLinkDownException>(() => rig.ReadAsync());
            Assert.Equal(2, rig.Pings);
            Assert.Equal(accepted, rig.Server.Accepted);

            rig.PingAnswer = true;
            rig.Now += TimeSpan.FromSeconds(5);
            Assert.Equal(7, await rig.ReadAsync());
            Assert.False(rig.Plc.IsLinkDown);
        }

        [Fact]
        public async Task WhenThePingNeverWorkedOnlyTheTurnsCount()
        {
            // ICMP blocked by a firewall: a silent ping says nothing.
            using var rig = await Rig.StartAsync(initialize: false);
            rig.PingAnswer = false;
            await rig.Plc.InitializeAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, rig.Pings);

            await rig.GoDownAsync();
            rig.Server.Start();

            rig.Now += TimeSpan.FromSeconds(1);
            Assert.Equal(7, await rig.ReadAsync());
            Assert.Equal(1, rig.Pings);   // no ping before the attempt
        }

        [Fact]
        public async Task APlcErrorIsAnAnswer()
        {
            using var rig = await Rig.StartAsync();

            var ex = await Assert.ThrowsAsync<OmronException>(() => rig.ReadAsync(FakeFinsServer.ErrorAddress));
            Assert.IsType<FINSException>(ex.InnerException);
            Assert.False(rig.Plc.IsLinkDown);
            Assert.Empty(rig.Events);
        }

        [Fact]
        public async Task TryReconnectNeverThrowsAndWaitsForItsTurn()
        {
            using var rig = await Rig.StartAsync();
            Assert.True(await rig.Plc.TryReconnectAsync(TestContext.Current.CancellationToken));

            await rig.GoDownAsync();
            Assert.False(await rig.Plc.TryReconnectAsync(TestContext.Current.CancellationToken));

            rig.Server.Start();
            Assert.False(await rig.Plc.TryReconnectAsync(TestContext.Current.CancellationToken));   // not due yet

            rig.Now += TimeSpan.FromSeconds(1);
            Assert.True(await rig.Plc.TryReconnectAsync(TestContext.Current.CancellationToken));
            Assert.False(rig.Plc.IsLinkDown);
            Assert.Equal(7, await rig.ReadAsync());
        }

        [Fact]
        public async Task TryReconnectBeforeTheInitializationIsFalse()
        {
            using var rig = await Rig.StartAsync(initialize: false);
            Assert.False(await rig.Plc.TryReconnectAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task AnExplicitInitializationIsNotHeldBack()
        {
            using var rig = await Rig.StartAsync(initialize: false);
            rig.Server.Stop();
            await Assert.ThrowsAsync<OmronException>(() => rig.Plc.InitializeAsync(TestContext.Current.CancellationToken));
            Assert.True(rig.Plc.IsLinkDown);

            rig.Server.Start();
            await rig.Plc.InitializeAsync(TestContext.Current.CancellationToken);   // same instant: no turn needed
            Assert.False(rig.Plc.IsLinkDown);
            Assert.Equal(7, await rig.ReadAsync());
        }

        [Fact]
        public async Task AHandlerThatThrowsDoesNotBreakTheCommunication()
        {
            using var rig = await Rig.StartAsync();
            rig.Plc.LinkStateChanged += (_, _) => throw new InvalidOperationException("a broken logger");

            await rig.GoDownAsync();
            rig.Server.Start();
            rig.Now += TimeSpan.FromSeconds(1);
            Assert.Equal(7, await rig.ReadAsync());
        }

        [Fact]
        public async Task ThePolicyCanBeSwitchedOffWhileDown()
        {
            using var rig = await Rig.StartAsync();
            await rig.GoDownAsync();
            rig.Server.Start();

            rig.Plc.ReconnectPolicy = null;
            Assert.Equal(7, await rig.ReadAsync());
            Assert.False(rig.Plc.IsLinkDown);
        }

        [Fact]
        public void APolicyThatMakesNoSenseIsRefused()
        {
            using var plc = new OmronPLC(50, 10, "127.0.0.1", 9600, Timeout, 1);
            Assert.ThrowsAny<ArgumentException>(() => plc.ReconnectPolicy = new ReconnectPolicy { Delays = Array.Empty<TimeSpan>() });
            Assert.ThrowsAny<ArgumentException>(() => plc.ReconnectPolicy = new ReconnectPolicy { Delays = new[] { TimeSpan.FromSeconds(-1) } });
            Assert.ThrowsAny<ArgumentException>(() => plc.ReconnectPolicy = new ReconnectPolicy { PingTimeoutMs = 0 });
            Assert.Null(plc.ReconnectPolicy);
        }

        [Fact]
        public void TheWaitGrowsOneTwoThreeThenFiveSeconds()
        {
            var t0 = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);
            var backoff = new ReconnectBackoff(ReconnectPolicy.Default.Delays);
            var now = t0;
            Assert.True(backoff.Failed(now));

            foreach (var step in new[] { 1, 2, 3, 5, 5 })
            {
                Assert.False(backoff.TryReserve(now + TimeSpan.FromSeconds(step - 0.1)));
                now += TimeSpan.FromSeconds(step);
                Assert.True(backoff.TryReserve(now));
                Assert.False(backoff.TryReserve(now));   // booked: the next caller waits
                Assert.False(backoff.Failed(now));
            }

            Assert.Equal(now - t0, backoff.Succeeded(now));
            Assert.False(backoff.IsDown);
        }
    }
}
