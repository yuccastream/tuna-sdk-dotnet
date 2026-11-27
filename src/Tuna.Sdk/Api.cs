using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tuna.Sdk;

internal sealed class ConnectRequest
{
    [JsonPropertyName("client_version")] public int ClientVersion { get; set; }
    [JsonPropertyName("client_name")] public string ClientName { get; set; } = "";
    [JsonPropertyName("sdk_version")] public string SdkVersion { get; set; } = "";
    [JsonPropertyName("forwards_to")] public string ForwardsTo { get; set; } = "";
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
    [JsonPropertyName("subdomain")] public string Subdomain { get; set; } = "";
    [JsonPropertyName("domain")] public string Domain { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("os")] public string Os { get; set; } = "";
    [JsonPropertyName("arch")] public string Arch { get; set; } = "";
    [JsonPropertyName("port_or_alias")] public string PortOrAlias { get; set; } = "";
    [JsonPropertyName("tunnel_session_id")] public string TunnelSessionId { get; set; } = "";
    [JsonPropertyName("proxy_protocol")] public int ProxyProtocol { get; set; }
    [JsonPropertyName("cidr_allow_list")] public List<string>? CidrAllowList { get; set; }
    [JsonPropertyName("cidr_deny_list")] public List<string>? CidrDenyList { get; set; }
}

internal sealed class HostKey
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
}

internal sealed class ConnectResponse
{
    [JsonPropertyName("tunnel_session_id")] public string TunnelSessionId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("banner")] public string Banner { get; set; } = "";
    [JsonPropertyName("public_url")] public string PublicUrl { get; set; } = "";
    [JsonPropertyName("domain_name")] public string DomainName { get; set; } = "";
    [JsonPropertyName("endpoint_port")] public uint EndpointPort { get; set; }
    [JsonPropertyName("host_keys")] public List<HostKey> HostKeys { get; set; } = new();

    internal string PublicUrlFor(string protocol)
    {
        if (PublicUrl.Length > 0)
        {
            return PublicUrl;
        }
        return protocol == "tcp" ? $"tcp://{DomainName}:{EndpointPort}" : "https://" + DomainName;
    }
}

internal sealed class ErrorBody
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("request_id")] public string? RequestId { get; set; }
}

internal sealed class NodeEvent
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectRequest))]
[JsonSerializable(typeof(ConnectResponse))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(NodeEvent))]
internal sealed partial class ApiJson : JsonSerializerContext
{
}

internal static class Api
{
    private const int MaxResponseSize = 1 << 20;
    internal static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);

    internal static HttpClient NewHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    internal static string Os() =>
        OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "darwin"
        : OperatingSystem.IsFreeBSD() ? "freebsd"
        : RuntimeInformation.OSDescription;

    internal static string Arch() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.X86 => "386",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        var a => a.ToString().ToLowerInvariant(),
    };

    internal static void CheckServerUrl(string s)
    {
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http") || u.Host.Length == 0)
        {
            throw new ArgumentException($"invalid server URL \"{s}\": want https://host");
        }
    }

    internal static async Task<ConnectResponse> ConnectAsync(HttpClient http, string serverUrl, ConnectRequest body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, serverUrl.TrimEnd('/') + "/v1/connect")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, ApiJson.Default.ConnectRequest), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", body.ClientName);
        req.Headers.ConnectionClose = true;
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var bytes = await ReadLimitedAsync(resp.Content, ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        if (status is < 200 or > 299)
        {
            throw ServerError(status, bytes, resp.Headers.Location?.OriginalString, resp.Headers.RetryAfter);
        }
        return JsonSerializer.Deserialize(bytes, ApiJson.Default.ConnectResponse) ?? throw new InvalidDataException("empty /v1/connect response");
    }

    internal static TunaServerException ServerError(int status, byte[] body, string? location, RetryConditionHeaderValue? retryAfterHeader)
    {
        TimeSpan? retryAfter = retryAfterHeader?.Delta is { } d && d > TimeSpan.Zero ? (d > MaxRetryAfter ? MaxRetryAfter : d) : null;
        if (status is >= 300 and < 400)
        {
            return new TunaServerException(status, $"server redirects to \"{location}\", check the server URL", retryAfter: retryAfter);
        }
        ErrorBody? eb = null;
        try
        {
            eb = JsonSerializer.Deserialize(body, ApiJson.Default.ErrorBody);
        }
        catch (JsonException)
        {
        }
        if (eb is null || string.IsNullOrEmpty(eb.Error))
        {
            return new TunaServerException(status, $"server responded with status {status}", eb?.Code, eb?.RequestId, retryAfter);
        }
        return new TunaServerException(status, eb.Error, eb.Code, eb.RequestId, retryAfter);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[16 << 10];
        int n;
        while (ms.Length < MaxResponseSize && (n = await s.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, MaxResponseSize - ms.Length)), ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}
