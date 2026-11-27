using System.Net;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Tuna.Sdk;

/// <summary>
/// A tuna.am tunnel that proxies connections arriving at its public address
/// to a local "host:port". It survives network failures: it reconnects with
/// backoff and keeps its public address. It ends on disposal, cancellation
/// or a permanent error (see <see cref="IsPermanent"/>).
/// </summary>
public sealed class TunaTunnel : IAsyncDisposable, IDisposable
{
    /// <summary>The SDK version.</summary>
    public const string Version = "0.1.0";

    /// <summary>The tuna.am API used when <see cref="TunaOptions.ServerUrl"/> is empty.</summary>
    public const string DefaultServerUrl = "https://api.tuna.am";

    private const int ClientVersion = 2;
    private const string SshUser = "tuna";
    private const string EventsCommand = "events";
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EventsDrainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);
    internal static TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);
    internal static TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(15);

    private readonly TunaOptions _o;
    private readonly string _apiKey;
    private readonly string _serverUrl;
    private readonly string _clientName;
    private readonly string _forwardsTo;
    private readonly string _localHost;
    private readonly int _localPort;
    private readonly HttpClient _http = Api.NewHttpClient();
    private readonly CancellationTokenSource _stop;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _emitLock = new();
    private volatile string _url = "";
    private bool _everUp;
    private bool _bannerShown;
    private string _sessionId = "";
    private Exception? _lastError;

    private TunaTunnel(TunaOptions o, string localAddr, CancellationToken ct)
    {
        _o = o;
        _apiKey = string.IsNullOrEmpty(o.ApiKey) ? Environment.GetEnvironmentVariable("TUNA_API_KEY") ?? "" : o.ApiKey;
        if (_apiKey.Length == 0)
        {
            throw new ArgumentException("API key is required: set TunaOptions.ApiKey or TUNA_API_KEY");
        }
        if (o.Protocol is not ("http" or "tcp"))
        {
            throw new ArgumentException($"unsupported protocol \"{o.Protocol}\"");
        }
        _serverUrl = string.IsNullOrEmpty(o.ServerUrl) ? DefaultServerUrl : o.ServerUrl;
        Api.CheckServerUrl(_serverUrl);
        _clientName = string.IsNullOrEmpty(o.ClientName) ? "tuna-sdk-dotnet/" + Version : o.ClientName;
        if (_clientName.Any(char.IsControl))
        {
            throw new ArgumentException("ClientName must not contain control characters");
        }
        (_localHost, _localPort) = ParseLocalAddr(localAddr);
        _forwardsTo = localAddr;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    }

    /// <summary>The current public address, e.g. https://name.tuna.am or tcp://ru.tuna.am:40000.</summary>
    public string Url => _url;

    /// <summary>
    /// Completes when the tunnel ends: successfully after disposal or
    /// cancellation, with the error after a permanent failure.
    /// </summary>
    public Task Completion => _done.Task;

    /// <summary>
    /// Connects a tunnel to <paramref name="localAddr"/> ("host:port") and
    /// returns once it is up. <paramref name="cancellationToken"/> bounds the
    /// whole life of the tunnel, not only the first connection. Use
    /// <see cref="TunaOptions.ConnectTimeout"/> to limit the wait for the first
    /// connection.
    /// </summary>
    /// <exception cref="ArgumentException">The options or the address are invalid.</exception>
    /// <exception cref="TunaServerException">The API refused the tunnel for good.</exception>
    /// <exception cref="TimeoutException">ConnectTimeout ran out before the tunnel came up.</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the tunnel came up.</exception>
    public static async Task<TunaTunnel> StartAsync(TunaOptions options, string localAddr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(localAddr);
        var t = new TunaTunnel(options, localAddr, cancellationToken);
        _ = Task.Run(t.RunAsync);
        var timeout = options.ConnectTimeout > TimeSpan.Zero ? Task.Delay(options.ConnectTimeout) : Task.Delay(Timeout.Infinite);
        var first = await Task.WhenAny(t._ready.Task, t._done.Task, timeout).ConfigureAwait(false);
        if (t._ready.Task.IsCompleted)
        {
            return t;
        }
        if (first == timeout)
        {
            t._stop.Cancel();
            await t._done.Task.ConfigureAwait(false);
            throw new TimeoutException(t.StartMessage($"tunnel did not come up within {options.ConnectTimeout}"), t._lastError);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(t.StartMessage("tunnel start was cancelled"), t._lastError, cancellationToken);
        }
        await t._done.Task.ConfigureAwait(false);
        throw new InvalidOperationException("tunnel ended before it came up");
    }

    /// <summary>
    /// Proxies tunnel connections to <paramref name="localAddr"/> ("host:port")
    /// until a permanent error occurs or <paramref name="cancellationToken"/> is
    /// cancelled. Cancellation after the tunnel came up returns normally;
    /// before that it throws like <see cref="StartAsync"/>.
    /// </summary>
    public static async Task ForwardAsync(TunaOptions options, string localAddr, CancellationToken cancellationToken = default)
    {
        await using var t = await StartAsync(options, localAddr, cancellationToken).ConfigureAwait(false);
        await t.Completion.ConfigureAwait(false);
    }

    /// <summary>Reports whether retrying after <paramref name="error"/> is pointless.</summary>
    public static bool IsPermanent(Exception? error) =>
        error is TunaTerminatedException || (error is TunaServerException se && se.Permanent);

    /// <summary>Closes the tunnel together with every connection it carries.</summary>
    public void Dispose() => _stop.Cancel();

    /// <summary>Closes the tunnel and waits until it has stopped reconnecting.</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _done.Task.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private string StartMessage(string what) =>
        _lastError is null ? what : $"{what} (last attempt: {_lastError.Message})";

    private void Emit(TunaEvent e)
    {
        if (_o.OnEvent is not { } handler)
        {
            return;
        }
        lock (_emitLock)
        {
            handler(e);
        }
    }

    private void Finish(Exception? error)
    {
        if (error is null)
        {
            _done.TrySetResult();
        }
        else
        {
            _done.TrySetException(error);
        }
        _stop.Cancel();
        _http.Dispose();
    }

    private async Task RunAsync()
    {
        var backoff = MinBackoff;
        while (true)
        {
            var (connected, error) = await SessionAsync().ConfigureAwait(false);
            if (_stop.IsCancellationRequested)
            {
                Finish(null);
                return;
            }
            if (!_everUp && error is TunaServerException se)
            {
                se.BeforeFirstConnect = true;
            }
            _lastError = error;
            if (IsPermanent(error))
            {
                if (error is TunaTerminatedException te)
                {
                    Emit(new TunaEvent(TunaEventType.Terminated) { Reason = te.Reason, Message = te.Message });
                }
                Finish(error);
                return;
            }
            if (connected)
            {
                backoff = MinBackoff;
            }
            var wait = RetryDelay(backoff, error);
            Emit(new TunaEvent(TunaEventType.Reconnecting) { Error = error, RetryIn = wait });
            try
            {
                await Task.Delay(wait, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Finish(null);
                return;
            }
            backoff = backoff * 2 > MaxBackoff ? MaxBackoff : backoff * 2;
        }
    }

    internal static TimeSpan RetryDelay(TimeSpan backoff, Exception? error)
    {
        var wait = MinBackoff + TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * backoff.Ticks));
        if (wait > MaxBackoff)
        {
            wait = MaxBackoff;
        }
        if (error is TunaServerException { RetryAfter: { } ra } && ra > wait)
        {
            wait = ra;
        }
        return wait;
    }

    private async Task<(bool Connected, Exception? Error)> SessionAsync()
    {
        ConnectResponse cr;
        try
        {
            cr = await Api.ConnectAsync(_http, _serverUrl, new ConnectRequest
            {
                ClientVersion = ClientVersion,
                ClientName = _clientName,
                SdkVersion = Version,
                ForwardsTo = _forwardsTo,
                Protocol = _o.Protocol,
                Location = _o.Location ?? "",
                Subdomain = _o.Subdomain ?? "",
                Domain = _o.Domain ?? "",
                Token = _apiKey,
                Os = Api.Os(),
                Arch = Api.Arch(),
                PortOrAlias = _o.Port ?? "",
                TunnelSessionId = _sessionId,
                ProxyProtocol = _o.ProxyProtocol,
                CidrAllowList = _o.CidrAllow.Count > 0 ? _o.CidrAllow.ToList() : null,
                CidrDenyList = _o.CidrDeny.Count > 0 ? _o.CidrDeny.ToList() : null,
            }, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return (false, e);
        }
        _sessionId = cr.TunnelSessionId;
        if (cr.Banner.Length > 0 && !_bannerShown)
        {
            _bannerShown = true;
            Emit(new TunaEvent(TunaEventType.Banner) { Message = cr.Banner });
        }

        var node = new NodeSession(this, cr);
        try
        {
            await node.ConnectAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            node.Fail(null);
            return (false, node.FailError ?? e);
        }

        _url = cr.PublicUrlFor(_o.Protocol);
        if (_everUp)
        {
            Emit(new TunaEvent(TunaEventType.Reconnected) { Url = _url });
        }
        else
        {
            _everUp = true;
            Emit(new TunaEvent(TunaEventType.Connected) { Url = _url });
            _ready.TrySetResult();
        }
        return (true, await node.Dead.ConfigureAwait(false));
    }

    internal static (string Host, int Port) ParseLocalAddr(string addr)
    {
        if (addr.StartsWith("unix://", StringComparison.Ordinal))
        {
            throw new ArgumentException("unix sockets are not supported by the .NET SDK, use host:port");
        }
        var colon = addr.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(addr.AsSpan(colon + 1), out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException($"local address \"{addr}\" is not host:port");
        }
        var host = addr[..colon];
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host[1..^1];
        }
        return (host, port);
    }

    internal static byte[] ParseAuthorizedKey(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2)
        {
            throw new FormatException("parse host key: not an authorized_keys line");
        }
        try
        {
            return Convert.FromBase64String(fields[1]);
        }
        catch (FormatException e)
        {
            throw new FormatException("parse host key: " + e.Message, e);
        }
    }

    private sealed class NodeSession
    {
        private readonly TunaTunnel _t;
        private readonly ConnectResponse _cr;
        private readonly TaskCompletionSource<Exception?> _dead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SshClient? _client;
        private Task _events = Task.CompletedTask;
        private bool _hostKeyRejected;
        private long _lastPong;
        private CancellationTokenRegistration _stopReg;

        internal NodeSession(TunaTunnel t, ConnectResponse cr)
        {
            _t = t;
            _cr = cr;
        }

        internal Task<Exception?> Dead => _dead.Task;

        internal Exception? FailError => _dead.Task.IsCompleted ? _dead.Task.Result : null;

        internal void Fail(Exception? error)
        {
            if (!_dead.TrySetResult(error))
            {
                return;
            }
            _stopReg.Unregister();
            var client = _client;
            if (client is not null)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                });
            }
        }

        internal async Task ConnectAsync()
        {
            var keys = _cr.HostKeys.Select(k => ParseAuthorizedKey(k.Key)).ToList();
            var (host, port) = ParseLocalAddr(_cr.Address);
            var info = new ConnectionInfo(host, port, SshUser, new PasswordAuthenticationMethod(SshUser, _cr.Token))
            {
                Timeout = DialTimeout,
            };
            var client = new SshClient(info) { KeepAliveInterval = KeepAliveInterval };
            _client = client;
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = keys.Any(k => k.AsSpan().SequenceEqual(e.HostKey));
                _hostKeyRejected = !e.CanTrust;
            };
            client.ErrorOccurred += (_, e) => Lost(e.Exception);
            _stopReg = _t._stop.Token.Register(() => Fail(null));

            try
            {
                await client.ConnectAsync(_t._stop.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_hostKeyRejected)
            {
                throw new SshConnectionException($"host key verification failed for {_cr.Address}");
            }

            var local = await ResolveAsync(_t._localHost).ConfigureAwait(false);
            var forward = new ForwardedPortRemote(IPAddress.Loopback, 0, local, (uint)_t._localPort);
            forward.Exception += (_, e) =>
            {
                if (e.Exception is not SshConnectionException && !_dead.Task.IsCompleted)
                {
                    _t.Emit(new TunaEvent(TunaEventType.LocalError) { Error = e.Exception });
                }
            };
            client.AddForwardedPort(forward);
            try
            {
                await Task.Run(forward.Start).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new SshException("remote forward: " + e.Message, e);
            }
            _events = Task.Run(() => Events(client));
        }

        private static async Task<IPAddress> ResolveAsync(string host)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                return ip;
            }
            var all = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            return all.Length > 0 ? all[0] : throw new IOException($"no address for {host}");
        }

        private void Lost(Exception cause)
        {
            Task.WhenAny(_events, _dead.Task).Wait(EventsDrainTimeout);
            Fail(new IOException("connection to node lost: " + cause.Message, cause));
        }

        private void Events(SshClient client)
        {
            using var cmd = client.CreateCommand(EventsCommand);
            Task exec;
            try
            {
                exec = cmd.ExecuteAsync();
            }
            catch (Exception)
            {
                return;
            }
            exec.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            Stream input;
            try
            {
                input = cmd.CreateInputStream();
            }
            catch (Exception)
            {
                return;
            }
            Interlocked.Exchange(ref _lastPong, Environment.TickCount64);
            using var keepAliveStop = new CancellationTokenSource();
            _ = KeepAliveAsync(input, keepAliveStop.Token);
            try
            {
                using var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    NodeEvent? ev;
                    try
                    {
                        ev = JsonSerializer.Deserialize(line, ApiJson.Default.NodeEvent);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    switch (ev?.Type)
                    {
                        case "pong":
                            Interlocked.Exchange(ref _lastPong, Environment.TickCount64);
                            break;
                        case "terminate":
                            Fail(new TunaTerminatedException(ev.Reason ?? ""));
                            return;
                        case "session_expiring":
                            _t.Emit(new TunaEvent(TunaEventType.SessionExpiring) { ExpiresIn = TimeSpan.FromSeconds(ev.ExpiresIn) });
                            break;
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                keepAliveStop.Cancel();
            }
        }

        private async Task KeepAliveAsync(Stream input, CancellationToken ct)
        {
            var ping = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}\n");
            var lastPing = Environment.TickCount64;
            try
            {
                while (!_dead.Task.IsCompleted)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    var now = Environment.TickCount64;
                    if (now - Interlocked.Read(ref _lastPong) >= (long)KeepAliveTimeout.TotalMilliseconds)
                    {
                        Fail(new KeepAliveTimeoutException());
                        return;
                    }
                    if (now - lastPing >= (long)KeepAliveInterval.TotalMilliseconds)
                    {
                        lastPing = now;
                        _ = Task.Run(() => input.Write(ping)).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
