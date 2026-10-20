using System.Diagnostics;

namespace RICADO.Omron.Tests
{
    /// <summary>
    /// The channel never stays stuck: after a fault the socket is thrown away and the next
    /// attempt opens a new one. Before, a failed reconnection left every following request
    /// refused ("must be Initialized first") even with the PLC answering again.
    /// </summary>
    public class ChannelRecoveryTests
    {
        private const int Timeout = 300;

        private static async Task<OmronPLC> ConnectAsync(FakeFinsServer server, int retries = 1, int timeout = Timeout)
        {
            var plc = new OmronPLC(50, 10, "127.0.0.1", server.Port, timeout, retries);
            await plc.InitializeAsync(TestContext.Current.CancellationToken);
            return plc;
        }

        private static async Task<ushort> ReadAsync(OmronPLC plc, ushort address = 100)
            => (await plc.ReadWordAsync(address, MemoryWordDataType.DataMemory, TestContext.Current.CancellationToken)).Values[0];

        [Fact]
        public async Task RequestsWorkAgainWhenThePlcComesBack()
        {
            using var server = new FakeFinsServer { [100] = 7 };
            using var plc = await ConnectAsync(server);
            Assert.Equal(7, await ReadAsync(plc));

            // Longer than a whole request: the reconnection inside it fails too.
            server.Stop();
            await Assert.ThrowsAsync<OmronException>(() => ReadAsync(plc));
            await Assert.ThrowsAsync<OmronException>(() => ReadAsync(plc));

            server.Start();
            Assert.Equal(7, await ReadAsync(plc));
        }

        [Fact]
        public async Task AConnectionClosedByThePlcIsReopenedWithinTheSameRequest()
        {
            using var server = new FakeFinsServer { [100] = 7 };
            using var plc = await ConnectAsync(server);

            server.DropConnections();
            Assert.Equal(7, await ReadAsync(plc));
            Assert.Equal(2, server.Accepted);
        }

        [Fact]
        public async Task AConnectionClosedDuringTheAnswerFailsAtOnce()
        {
            // Before, the receive loop spun on zero bytes until the timeout.
            using var server = new FakeFinsServer();
            using var plc = await ConnectAsync(server, retries: 0, timeout: 3000);
            server.Mode = FakeFinsServer.Behavior.CloseOnRequest;

            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<OmronException>(() => ReadAsync(plc));
            Assert.True(clock.ElapsedMilliseconds < 1500, $"took {clock.ElapsedMilliseconds} ms");
        }

        [Fact]
        public async Task AnUnreachablePlcFailsWithinTheTimeout()
        {
            // 192.0.2.1 (TEST-NET-1) never answers: without its own limit the TCP connection
            // waits as long as Windows decides, about 20 s. Without a route it fails at once.
            using var plc = new OmronPLC(50, 10, "192.0.2.1", 9600, Timeout, 0);

            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<OmronException>(() => plc.InitializeAsync(TestContext.Current.CancellationToken));
            Assert.True(clock.ElapsedMilliseconds < Timeout * 4, $"took {clock.ElapsedMilliseconds} ms");
        }

        [Fact]
        public async Task AMutePlcFailsWithinTheTimeoutAndIsRetriedOnANewSocket()
        {
            using var server = new FakeFinsServer { [100] = 7 };
            using var plc = await ConnectAsync(server, retries: 0);

            server.Mode = FakeFinsServer.Behavior.Mute;
            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<OmronException>(() => ReadAsync(plc));
            Assert.True(clock.ElapsedMilliseconds < Timeout * 4, $"took {clock.ElapsedMilliseconds} ms");

            server.Mode = FakeFinsServer.Behavior.Normal;
            Assert.Equal(7, await ReadAsync(plc));
            Assert.Equal(2, server.Accepted);
        }

        [Fact]
        public async Task TheAnswerToAnotherRequestDropsTheSocketAndTheRequestIsRetried()
        {
            using var server = new FakeFinsServer { [100] = 7 };
            using var plc = await ConnectAsync(server);

            server.StaleResponseOnce = true;
            Assert.Equal(7, await ReadAsync(plc));
            Assert.Equal(2, server.Accepted);
        }

        [Fact]
        public async Task AfterAReconnectionTheFramesCarryTheNewClientNode()
        {
            using var server = new FakeFinsServer();
            using var plc = await ConnectAsync(server);

            server.DropConnections();
            await ReadAsync(plc);
            await ReadAsync(plc);
            Assert.Equal(2, server.Accepted);
            Assert.Equal(0, server.WrongSourceNodeFrames);
        }

        [Fact]
        public async Task APlcErrorKeepsTheConnection()
        {
            using var server = new FakeFinsServer { [100] = 7 };
            using var plc = await ConnectAsync(server);

            var ex = await Assert.ThrowsAsync<OmronException>(() => ReadAsync(plc, FakeFinsServer.ErrorAddress));
            Assert.IsType<FINSException>(ex.InnerException);

            Assert.Equal(7, await ReadAsync(plc));
            Assert.Equal(1, server.Accepted);
        }

        [Fact]
        public async Task TheFailureSaysWhyInsteadOfMaxRetries()
        {
            using var server = new FakeFinsServer();
            using var plc = await ConnectAsync(server);
            server.Stop();

            var ex = await Assert.ThrowsAsync<OmronException>(() => ReadAsync(plc));
            Assert.NotNull(ex.InnerException);
            Assert.DoesNotContain("Max retries", ex.Message);
        }

        [Fact]
        public async Task FailedInitializationsLeaveNoSocketOpen()
        {
            using var server = new FakeFinsServer { Mode = FakeFinsServer.Behavior.Mute };
            using var plc = new OmronPLC(50, 10, "127.0.0.1", server.Port, Timeout, 0);

            // A socket left open would keep the count above zero for good.
            for (int i = 0; i < 3; i++)
            {
                await Assert.ThrowsAsync<OmronException>(() => plc.InitializeAsync(TestContext.Current.CancellationToken));
                await WaitUntilAsync(() => server.OpenConnections == 0);
            }

            Assert.Equal(3, server.Accepted);
        }

        [Fact]
        public async Task RequestsInParallelDuringAnOutageShareOneNewConnection()
        {
            using var server = new FakeFinsServer { [100] = 7 };
            using var plc = await ConnectAsync(server, timeout: 2000);

            server.DropConnections();
            var reads = Enumerable.Range(0, 8).Select(_ => ReadAsync(plc)).ToArray();
            var values = await Task.WhenAll(reads);

            Assert.All(values, v => Assert.Equal(7, v));
            Assert.Equal(2, server.Accepted);
            Assert.Equal(1, server.MaxOpenConnections);
        }

        [Fact]
        public async Task WritesGoThroughAfterAReconnection()
        {
            using var server = new FakeFinsServer();
            using var plc = await ConnectAsync(server);

            server.Stop();
            await Assert.ThrowsAsync<OmronException>(() => plc.WriteWordAsync(5, 200, MemoryWordDataType.DataMemory, TestContext.Current.CancellationToken));
            server.Start();

            await plc.WriteWordAsync(42, 200, MemoryWordDataType.DataMemory, TestContext.Current.CancellationToken);
            Assert.Equal(42, server[200]);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var clock = Stopwatch.StartNew();
            while (!condition() && clock.ElapsedMilliseconds < 3000)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.True(condition());
        }
    }
}
