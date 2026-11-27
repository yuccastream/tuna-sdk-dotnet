# tuna-sdk-dotnet

Open [tuna.am](https://tuna.am) tunnels straight from .NET code: expose a
local HTTP or TCP service at a public address without installing the `tuna`
CLI.

- `TunaTunnel.ForwardAsync` proxies connections arriving at the public
  address to a local `host:port`.
- Survives network failures: reconnects with backoff and keeps the same
  public address.
- Small: only [SSH.NET](https://github.com/sshnet/SSH.NET) and the base
  class library.

The API is not stable yet: it may change before v1.

## Install

```shell
dotnet add package Tuna.Sdk
```

The namespace is `Tuna.Sdk`. Targets .NET 8 or later.

## Authentication

Create an API key with the `tunnels:connect` scope at
[my.tuna.am](https://my.tuna.am) and pass it in `TunaOptions.ApiKey` or the
`TUNA_API_KEY` environment variable.

## Forward to a local service

```csharp
using Tuna.Sdk;

var options = new TunaOptions
{
    Protocol = "http",
    OnEvent = e =>
    {
        if (e.Type == TunaEventType.Connected)
        {
            Console.WriteLine($"forwarding {e.Url} -> 127.0.0.1:5000");
        }
    },
};
await TunaTunnel.ForwardAsync(options, "127.0.0.1:5000", cancellationToken);
```

`ForwardAsync` runs until `cancellationToken` is cancelled or the tunnel ends
for good. To keep a handle on the tunnel, start it and await `Completion`
later:

```csharp
await using var tunnel = await TunaTunnel.StartAsync(options, "127.0.0.1:5432");
Console.WriteLine(tunnel.Url);
await tunnel.Completion;
```

The local address is `host:port`; Unix sockets are not supported.
[`examples/Runner`](examples/Runner/Program.cs) is a complete command-line
client built on `ForwardAsync`.

## Options

| Property | Meaning |
|---|---|
| `Protocol` | `http` or `tcp`, required |
| `ApiKey` | API key, defaults to `$TUNA_API_KEY` |
| `Subdomain`, `Domain` | reserved subdomain or custom domain for HTTP |
| `Port` | reserved TCP port or its alias |
| `ProxyProtocol` | PROXY protocol header version for TCP: `1` or `2` |
| `CidrAllow`, `CidrDeny` | client CIDRs filtered by the tuna.am node, TCP only |
| `Location` | only for names and ports reserved in the legacy `ru` or `nl` zones; empty lets the server pick |
| `OnEvent` | callback for tunnel events |
| `ConnectTimeout` | limit on the wait for the first connection; zero means no limit |
| `ServerUrl`, `ClientName` | API address, client name reported to the server |

## Events and errors

`OnEvent` receives `Connected`, `Reconnecting`, `Reconnected`, `Banner`,
`SessionExpiring`, `Terminated` and `LocalError`. Calls never overlap and run
on the tunnel's own threads, so keep the callback short.

Network failures and temporary server errors are retried: the pause grows from
1 to 15 seconds with random jitter, and `Retry-After` is respected. A lost
connection to the node is detected within 15 seconds and reported as
`KeepAliveTimeoutException` in a `Reconnecting` event. The tunnel ends with an
exception when retrying is pointless; check it with `TunaTunnel.IsPermanent`:

- `TunaServerException` — the API rejected the request: bad or revoked key,
  missing scope, unknown reserved name, unpaid plan. `Status`, `Code` and
  `RequestId` say why;
- `TunaTerminatedException` — the server closed the tunnel: it was deleted,
  suspended or hit the session time limit. `Reason` says why.

## Behaviour worth knowing

- The cancellation token passed to `StartAsync` or `ForwardAsync` bounds the
  whole life of the tunnel, not only the first connection. Cancelling it, or
  disposing the tunnel, closes every connection the tunnel carries. To give up
  early when the tunnel does not come up, set `ConnectTimeout`.
- The `Host` header and the PROXY protocol header reach your service as sent.
- The API client is `HttpClient` and honours `HTTPS_PROXY`. Connections to
  tuna.am nodes are plain SSH over TCP and do not use the proxy.

## Contributing

This repository is a read-only mirror of the SDK directory in the tuna.am
monorepo. Issues and pull requests are welcome here: accepted changes are
applied upstream and come back with the next release.

## License

[MIT](LICENSE)
