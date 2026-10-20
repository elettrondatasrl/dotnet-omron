using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RICADO.Omron.Tests
{
    /// <summary>
    /// A fake Omron PLC speaking FINS/TCP on loopback: node address negotiation, CPU unit data,
    /// memory area read and write. It can do what a real PLC does on a bad day: refuse
    /// connections (stopped), drop them, accept and never answer, answer with the reply of an
    /// older request, answer with a PLC error.
    /// </summary>
    internal sealed class FakeFinsServer : IDisposable
    {
        public enum Behavior
        {
            /// <summary>Answers every request.</summary>
            Normal,
            /// <summary>Accepts the connection and never answers anything (a PLC hung while rebooting).</summary>
            Mute,
            /// <summary>Closes the connection as soon as a FINS frame arrives.</summary>
            CloseOnRequest,
        }

        public const byte ServerNode = 10;

        /// <summary>Area addresses in this range answer with a PLC error (end code 0x1103, address range).</summary>
        public const ushort ErrorAddress = 1234;

        private readonly object _sync = new();
        private readonly List<TcpClient> _connections = new();
        private readonly ushort[] _memory = new ushort[32768];
        private TcpListener _listener;
        private volatile bool _disposed;
        private byte _nextClientNode = 100;

        public FakeFinsServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            StartAccepting(_listener);
        }

        public int Port { get; }

        public volatile Behavior Mode = Behavior.Normal;

        /// <summary>The next FINS frame is answered with the Service ID of an older request.</summary>
        public volatile bool StaleResponseOnce;

        /// <summary>Connections accepted since the start.</summary>
        public int Accepted;

        /// <summary>Most connections open at the same time.</summary>
        public int MaxOpenConnections;

        /// <summary>Connections open right now: dropped by the server, or closed by the client and noticed.</summary>
        public int OpenConnections { get { lock (_sync) return _connections.Count; } }

        /// <summary>FINS frames whose source node was not the one assigned to their connection.</summary>
        public int WrongSourceNodeFrames;

        /// <summary>FINS frames received.</summary>
        public int Frames;

        public ushort this[int address]
        {
            get { lock (_sync) return _memory[address]; }
            set { lock (_sync) _memory[address] = value; }
        }

        /// <summary>The PLC goes away: connections dropped, new ones refused.</summary>
        public void Stop()
        {
            lock (_sync)
            {
                _listener?.Stop();
                _listener = null;
            }
            DropConnections();
        }

        /// <summary>The PLC is back on the same port.</summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_listener != null) return;
                var listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Start();
                _listener = listener;
                StartAccepting(listener);
            }
        }

        /// <summary>The PLC closes the open connections (a reset of its Ethernet unit).</summary>
        public void DropConnections()
        {
            TcpClient[] open;
            lock (_sync)
            {
                open = _connections.ToArray();
                _connections.Clear();
            }
            foreach (var c in open)
            {
                try { c.Client.Shutdown(SocketShutdown.Both); } catch { }
                try { c.Dispose(); } catch { }
            }
        }

        private void StartAccepting(TcpListener listener)
        {
            new Thread(() =>
            {
                while (!_disposed)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch { return; }   // listener stopped

                    byte node;
                    lock (_sync)
                    {
                        _connections.Add(client);
                        MaxOpenConnections = Math.Max(MaxOpenConnections, _connections.Count);
                        node = _nextClientNode;
                        _nextClientNode = (byte)(_nextClientNode == 250 ? 100 : _nextClientNode + 1);
                    }
                    Interlocked.Increment(ref Accepted);

                    new Thread(() => Serve(client, node)) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        private void Serve(TcpClient client, byte clientNode)
        {
            try
            {
                var stream = client.GetStream();
                while (!_disposed)
                {
                    var header = ReadExactly(stream, 16);
                    if (header == null) break;
                    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
                    byte command = header[11];
                    var payload = ReadExactly(stream, length - 8);
                    if (payload == null) break;

                    if (Mode == Behavior.Mute) continue;

                    if (command == 0)   // node address request
                    {
                        var answer = new byte[8];
                        answer[3] = clientNode;
                        answer[7] = ServerNode;
                        Write(stream, 1, answer);
                        continue;
                    }

                    if (command != 2) break;

                    Interlocked.Increment(ref Frames);
                    if (Mode == Behavior.CloseOnRequest) break;
                    if (payload[7] != clientNode) Interlocked.Increment(ref WrongSourceNodeFrames);

                    byte serviceId = payload[9];
                    if (StaleResponseOnce)
                    {
                        StaleResponseOnce = false;
                        serviceId = unchecked((byte)(serviceId - 1));
                    }
                    Write(stream, 2, Answer(payload, serviceId));
                }
            }
            catch
            {
                // connection dropped
            }
            finally
            {
                lock (_sync) _connections.Remove(client);
                try { client.Dispose(); } catch { }
            }
        }

        private byte[] Answer(byte[] request, byte serviceId)
        {
            byte mrc = request[10], src = request[11];
            byte[] data = Array.Empty<byte>();
            ushort endCode = 0;

            if (mrc == 0x05 && src == 0x01)
            {
                data = CpuUnitData();
            }
            else if (mrc == 0x01 && (src == 0x01 || src == 0x02))
            {
                ushort address = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(13, 2));
                ushort count = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(16, 2));
                if (address == ErrorAddress)
                {
                    endCode = 0x1103;
                }
                else if (src == 0x01)
                {
                    data = new byte[count * 2];
                    lock (_sync)
                        for (int i = 0; i < count; i++)
                            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * 2, 2), _memory[(address + i) % _memory.Length]);
                }
                else
                {
                    lock (_sync)
                        for (int i = 0; i < count; i++)
                            _memory[(address + i) % _memory.Length] = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(18 + i * 2, 2));
                }
            }
            else
            {
                endCode = 0x0401;   // undefined command
            }

            var answer = new byte[14 + data.Length];
            answer[0] = 0xC0;           // ICF: response
            answer[2] = 0x02;           // GCT
            answer[4] = request[7];     // DA1 = the source of the request
            answer[7] = ServerNode;     // SA1
            answer[9] = serviceId;
            answer[10] = mrc;
            answer[11] = src;
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(12, 2), endCode);
            data.CopyTo(answer, 14);
            return answer;
        }

        /// <summary>A CJ2M: model, version, reserved area, area data.</summary>
        private static byte[] CpuUnitData()
        {
            var data = new byte[20 + 20 + 40 + 12];
            Encoding.ASCII.GetBytes("CJ2M-CPU33").CopyTo(data, 0);
            Encoding.ASCII.GetBytes("02.01").CopyTo(data, 20);
            data[41] = 1;                   // EM bank count
            data[80] = 0; data[81] = 60;    // program area 60 KW
            data[82] = 23;                  // bit areas
            data[83] = 0x80; data[84] = 0;  // DM 32768
            data[85] = 8;                   // timers
            data[86] = 1;                   // EM banks non file
            return data;
        }

        private static void Write(NetworkStream stream, byte command, byte[] payload)
        {
            var frame = new byte[16 + payload.Length];
            Encoding.ASCII.GetBytes("FINS").CopyTo(frame, 0);
            BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4, 4), (uint)(8 + payload.Length));
            frame[11] = command;
            payload.CopyTo(frame, 16);
            stream.Write(frame, 0, frame.Length);
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) return null;
                read += n;
            }
            return buffer;
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }
    }
}
