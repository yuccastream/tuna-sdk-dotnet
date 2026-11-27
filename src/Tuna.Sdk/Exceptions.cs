namespace Tuna.Sdk;

/// <summary>An error response from the tuna.am API.</summary>
public sealed class TunaServerException : Exception
{
    internal TunaServerException(int status, string message, string? code = null, string? requestId = null, TimeSpan? retryAfter = null)
        : base(requestId is { Length: > 0 } ? $"{message} (request id {requestId})" : message)
    {
        Status = status;
        Code = code ?? "";
        ServerMessage = message;
        RequestId = requestId ?? "";
        RetryAfter = retryAfter;
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The machine-readable error code.</summary>
    public string Code { get; }

    /// <summary>The human-readable error text.</summary>
    public string ServerMessage { get; }

    /// <summary>Identifies the request for tuna.am support.</summary>
    public string RequestId { get; }

    /// <summary>The delay requested by the server, if any.</summary>
    public TimeSpan? RetryAfter { get; }

    internal bool BeforeFirstConnect { get; set; }

    internal bool Permanent
    {
        get
        {
            if (Status is >= 300 and < 400)
            {
                return true;
            }
            if (BeforeFirstConnect && Status is >= 400 and < 500 and not 408 and not 429)
            {
                return true;
            }
            return Code is "PaymentRequired" or "Unauthorized" or "AuthorizationRequired" or "Forbidden" or "BadRequest" or "NotFound" or "UpgradeRequired";
        }
    }
}

/// <summary>The server closed the tunnel for good.</summary>
public sealed class TunaTerminatedException : Exception
{
    internal TunaTerminatedException(string reason) : base(Describe(reason))
    {
        Reason = reason;
    }

    /// <summary>The machine-readable cause, e.g. "deleted" or "session_limit".</summary>
    public string Reason { get; }

    internal static string Describe(string reason) => reason switch
    {
        "deleted" => "tunnel was deleted",
        "suspended" => "tunnel was suspended",
        "session_limit" => "session time limit reached",
        "session_ended" => "client session ended",
        "reconnect" => "tunnel was taken over by another connection",
        _ => $"tunnel terminated by server ({reason})",
    };
}

/// <summary>The node stopped answering keepalive pings.</summary>
public sealed class KeepAliveTimeoutException : Exception
{
    internal KeepAliveTimeoutException() : base("keepalive timeout")
    {
    }
}
