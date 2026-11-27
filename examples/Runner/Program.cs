using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tuna.Sdk;

const string Usage = """
    Usage:
      Runner http <addr> [-s SUBDOMAIN] [-d DOMAIN] [--log-format text|json]
      Runner tcp <addr> [-p PORT] [--proxy-protocol N] [--cidr-allow CIDR]... [--cidr-deny CIDR]... [--log-format text|json]

    addr: 8080, 10.0.0.1:8080, http://localhost:8080
    Environment: TUNA_API_KEY, TUNA_ADDR, TUNA_LOCATION
    """;

if (args.Length < 2 || args[0] is not ("http" or "tcp"))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var options = new TunaOptions
{
    Protocol = args[0],
    ServerUrl = Environment.GetEnvironmentVariable("TUNA_ADDR"),
    Location = Environment.GetEnvironmentVariable("TUNA_LOCATION"),
};
string? addr = null;
var json = false;
for (var i = 1; i < args.Length; i++)
{
    string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"flag {args[i - 1]} needs a value");
    switch (args[i])
    {
        case "-s": options.Subdomain = Value(); break;
        case "-d": options.Domain = Value(); break;
        case "-p": options.Port = Value(); break;
        case "--proxy-protocol": options.ProxyProtocol = int.Parse(Value(), CultureInfo.InvariantCulture); break;
        case "--cidr-allow": options.CidrAllow.Add(Value()); break;
        case "--cidr-deny": options.CidrDeny.Add(Value()); break;
        case "--log-format": json = Value() == "json"; break;
        case var a when a.StartsWith('-'):
            Console.Error.WriteLine($"Error: unknown flag {a}");
            return 2;
        case var a when addr is null: addr = a; break;
        default:
            Console.Error.WriteLine(Usage);
            return 2;
    }
}
if (addr is null)
{
    Console.Error.WriteLine(Usage);
    return 2;
}
addr = LocalAddr(addr);

var stdout = Console.OpenStandardOutput();
var print = (TunaEvent e) => Print(e.Type switch
{
    TunaEventType.Connected => "connected",
    TunaEventType.Reconnected => "reconnected",
    TunaEventType.Reconnecting => "reconnecting",
    TunaEventType.Banner => "banner",
    TunaEventType.SessionExpiring => "session_expiring",
    TunaEventType.Terminated => "terminated",
    _ => "local_error",
}, e);
options.OnEvent = print;

using var cts = new CancellationTokenSource();
using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cts.Cancel(); });
using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; cts.Cancel(); });

try
{
    await TunaTunnel.ForwardAsync(options, addr, cts.Token);
    return 0;
}
catch (Exception) when (cts.IsCancellationRequested)
{
    return 0;
}
catch (TunaTerminatedException)
{
    return 1;
}
catch (Exception e)
{
    Print("error", new TunaEvent(TunaEventType.LocalError) { Error = e });
    return 1;
}

void Print(string kind, TunaEvent e)
{
    if (!json)
    {
        Console.Error.WriteLine(kind switch
        {
            "connected" or "reconnected" => $"Forwarding {e.Url} -> {addr}",
            "reconnecting" => $"Connection error ({e.Error?.Message}), retry in {e.RetryIn.TotalSeconds:0.0}s",
            "session_expiring" => $"Session expires in {e.ExpiresIn}",
            "banner" or "terminated" => e.Message,
            _ => $"Error: {e.Error?.Message}",
        });
        return;
    }
    lock (stdout)
    {
        using (var w = new Utf8JsonWriter(stdout))
        {
            w.WriteStartObject();
            w.WriteString("time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            w.WriteString("event", kind);
            Field(w, "url", e.Url);
            Field(w, "message", e.Message);
            Field(w, "reason", e.Reason);
            Field(w, "error", e.Error?.Message);
            if (e.RetryIn > TimeSpan.Zero)
            {
                w.WriteNumber("retry_in", e.RetryIn.TotalSeconds);
            }
            if (e.ExpiresIn > TimeSpan.Zero)
            {
                w.WriteNumber("expires_in", e.ExpiresIn.TotalSeconds);
            }
            w.WriteEndObject();
        }
        stdout.WriteByte((byte)'\n');
        stdout.Flush();
    }
}

static void Field(Utf8JsonWriter w, string name, string? value)
{
    if (!string.IsNullOrEmpty(value))
    {
        w.WriteString(name, value);
    }
}

static string LocalAddr(string s)
{
    if (s.Contains("://", StringComparison.Ordinal) && !s.StartsWith("unix://", StringComparison.Ordinal))
    {
        var u = new Uri(s);
        if (u.Scheme != "http")
        {
            throw new ArgumentException($"scheme \"{u.Scheme}\" is not supported");
        }
        return $"{u.Host}:{u.Port}";
    }
    return int.TryParse(s, out _) ? "127.0.0.1:" + s : s;
}
