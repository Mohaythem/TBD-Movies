using System;

namespace TBDMovies.Application.Subtitles;

public enum SubtitleFailureKind
{
    NotConfigured,
    Authentication,
    RateLimited,
    Network,
    Timeout,
    Api,
    InvalidResponse
}

/// <summary>
/// Categorized subtitle provider exception safe for mapping to localized presentation strings
/// without exposing raw URLs, query parameters, or filesystem paths.
/// </summary>
public sealed class SubtitleServiceException : Exception
{
    public SubtitleServiceException(
        SubtitleFailureKind kind,
        string message,
        int? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
    }

    public SubtitleFailureKind Kind { get; }

    public int? StatusCode { get; }
}
