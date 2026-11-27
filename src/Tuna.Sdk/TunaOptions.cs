namespace Tuna.Sdk;

/// <summary>Configures a tunnel. Only <see cref="Protocol"/> is required.</summary>
public sealed class TunaOptions
{
    /// <summary>A tuna.am API key with the tunnels:connect scope. Defaults to the TUNA_API_KEY environment variable.</summary>
    public string? ApiKey { get; set; }

    /// <summary>"http" or "tcp".</summary>
    public string Protocol { get; set; } = "";

    /// <summary>Needed only for names and ports reserved in the legacy "ru" or "nl" zones. Empty lets the server pick the zone.</summary>
    public string? Location { get; set; }

    /// <summary>A reserved subdomain for HTTP tunnels.</summary>
    public string? Subdomain { get; set; }

    /// <summary>A custom domain for HTTP tunnels.</summary>
    public string? Domain { get; set; }

    /// <summary>A reserved TCP port or its alias.</summary>
    public string? Port { get; set; }

    /// <summary>Enables the PROXY protocol header (1 or 2) for TCP tunnels.</summary>
    public int ProxyProtocol { get; set; }

    /// <summary>Client CIDRs allowed to reach a TCP tunnel.</summary>
    public IList<string> CidrAllow { get; set; } = new List<string>();

    /// <summary>Client CIDRs rejected by a TCP tunnel.</summary>
    public IList<string> CidrDeny { get; set; } = new List<string>();

    /// <summary>The tuna.am API address. Defaults to <see cref="TunaTunnel.DefaultServerUrl"/>.</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Reported to the server. Defaults to "tuna-sdk-dotnet/&lt;Version&gt;".</summary>
    public string? ClientName { get; set; }

    /// <summary>
    /// Limits how long the tunnel may take to come up for the first time.
    /// Zero means no limit. Reconnects are not limited.
    /// </summary>
    public TimeSpan ConnectTimeout { get; set; }

    /// <summary>
    /// Receives tunnel events. Calls never overlap, but they come from the
    /// tunnel's own threads, so the handler must return quickly.
    /// </summary>
    public Action<TunaEvent>? OnEvent { get; set; }
}

/// <summary>The kind of a <see cref="TunaEvent"/>.</summary>
public enum TunaEventType
{
    /// <summary>The tunnel came up for the first time.</summary>
    Connected,

    /// <summary>The tunnel came up again after a failure.</summary>
    Reconnected,

    /// <summary>An attempt failed or the connection was lost; sent before the next attempt.</summary>
    Reconnecting,

    /// <summary>A server message for the user.</summary>
    Banner,

    /// <summary>The session time limit is near.</summary>
    SessionExpiring,

    /// <summary>The server closed the tunnel for good.</summary>
    Terminated,

    /// <summary>The local address could not be reached.</summary>
    LocalError,
}

/// <summary>Describes a change in the tunnel state.</summary>
/// <param name="Type">The kind of the event.</param>
public sealed record TunaEvent(TunaEventType Type)
{
    /// <summary>The public address, set for Connected and Reconnected.</summary>
    public string? Url { get; init; }

    /// <summary>Human-readable text, set for Banner and Terminated.</summary>
    public string? Message { get; init; }

    /// <summary>The machine-readable cause, set for Terminated.</summary>
    public string? Reason { get; init; }

    /// <summary>The cause, set for Reconnecting and LocalError.</summary>
    public Exception? Error { get; init; }

    /// <summary>The delay before the next attempt, set for Reconnecting.</summary>
    public TimeSpan RetryIn { get; init; }

    /// <summary>The time left, set for SessionExpiring.</summary>
    public TimeSpan ExpiresIn { get; init; }
}
