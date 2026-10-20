# RICADO.Omron
An Omron PLC Communication Library for .NET 10 Applications (FINS over TCP).

## When the PLC stops answering

The channel never stays stuck. A socket that failed once is thrown away, and the next attempt
opens a new one: a retry of the same request (`retries`, default 1), or the next request. So when
the PLC comes back after a reboot or a cable pulled for a minute, requests work again by
themselves, with no new `OmronPLC` and no new `InitializeAsync`.

Every step has a time limit, the `timeout` of the constructor (default 2000 ms): opening the
socket, the node negotiation, each answer. A socket closed by the PLC fails at once. An answer
carrying the Service ID of another request (left in the socket by a timeout) drops the socket and
the request is repeated on a new one. After a reconnection the frames carry the client node the
PLC assigned to the new connection.

A FINS error answer from the PLC (address out of range, wrong mode...) is an answer: it arrives
as an `OmronException` whose `InnerException` is a `FINSException`, and the connection stays.

## Reconnect policy (opt-in)

Without a policy every request goes to the network, also while the PLC is off: each one costs a
connection attempt and its exceptions. With a policy:

```csharp
var plc = new OmronPLC(50, 10, "192.168.250.1", 9600, timeout: 2000, retries: 1)
{
    ReconnectPolicy = ReconnectPolicy.Default,   // waits 1, 2, 3 s, then every 5; ping on
};
plc.LinkStateChanged += (_, e) =>
    log.Info(e.IsDown ? $"PLC down: {e.Cause.Message}" : $"PLC back after {e.DownFor}");
await plc.InitializeAsync(cancellationToken);
```

- A request that gets no answer marks the PLC as down (`IsLinkDown`, `LinkDownFor`,
  `LinkStateChanged`).
- While down, requests fail at once with `PlcLinkDownException` (an `OmronException`), without
  touching the network: nothing was sent, so retrying later is safe, writes included.
- When the next attempt is due, one caller gets it. If the PLC answered a ping while up, the
  attempt is made only if it answers the ping now: a PLC that is off costs nothing. With ICMP
  blocked only the delays apply.
- The first answer from the PLC brings it back up.
- `InitializeAsync` is an explicit attempt and is never held back.
- `TryReconnectAsync()` is for periodic loops: true when the connection is open or was just
  reopened, false while down and not due. It does not throw.

Delays and ping can be changed: `new ReconnectPolicy { Delays = new[] { TimeSpan.FromSeconds(2) },
UsePing = false }`. Setting the policy to null switches it off.
