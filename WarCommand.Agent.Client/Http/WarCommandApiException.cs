namespace WarCommand.Agent.Client.Http;

/// <summary>
/// Every non-2xx response and every transport failure, as one type carrying the contract's
/// <c>code</c> and the correlation id.
/// </summary>
public class WarCommandApiException : Exception
{
    public WarCommandApiException(ApiError error, string? requestCorrelationId = null, Exception? innerException = null)
        : base(Describe(error), innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
        RequestCorrelationId = requestCorrelationId;
    }

    public WarCommandApiException()
        : this(new ApiError { Code = ErrorCodes.TransportFailure })
    {
    }

    public WarCommandApiException(string message)
        : base(message)
    {
        Error = new ApiError { Code = ErrorCodes.TransportFailure, Detail = message };
    }

    public WarCommandApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = new ApiError { Code = ErrorCodes.TransportFailure, Detail = message };
    }

    public ApiError Error { get; }

    /// <summary>The contract's error code. Never branch on <see cref="ApiError.Detail"/>.</summary>
    public string Code => Error.Code;

    public int Status => Error.Status;

    /// <summary>From the body, falling back to the header, falling back to what we sent.</summary>
    public string? CorrelationId => Error.CorrelationId ?? RequestCorrelationId;

    /// <summary>The X-Correlation-Id this agent put on the request.</summary>
    public string? RequestCorrelationId { get; }

    /// <summary>Present on 429. Honour it rather than backing off blind.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>True when replaying the same call later could still succeed.</summary>
    public bool IsTransient => Status is 0 or 408 or 429 or >= 500;

    private static string Describe(ApiError error) =>
        error.Detail is { Length: > 0 } detail
            ? $"{error.Code} ({error.Status}): {detail}"
            : $"{error.Code} ({error.Status})";
}
