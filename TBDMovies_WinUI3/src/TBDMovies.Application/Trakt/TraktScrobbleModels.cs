using System.Text.Json.Serialization;

namespace TBDMovies.Application.Trakt;

public enum TraktScrobbleAction
{
    Start,
    Pause,
    Stop
}

public sealed record TraktScrobbleMovie(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("ids")] TraktSyncIds Ids);

public sealed record TraktScrobbleShow(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("ids")] TraktSyncIds Ids);

public sealed record TraktScrobbleEpisode(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("ids")] TraktSyncIds? Ids = null);

public sealed record TraktScrobblePayload(
    [property: JsonPropertyName("movie")] TraktScrobbleMovie? Movie = null,
    [property: JsonPropertyName("show")] TraktScrobbleShow? Show = null,
    [property: JsonPropertyName("episode")] TraktScrobbleEpisode? Episode = null,
    [property: JsonPropertyName("progress")] double Progress = 0.0,
    [property: JsonPropertyName("app_version")] string? AppVersion = null,
    [property: JsonPropertyName("app_date")] string? AppDate = null);

public sealed record TraktScrobbleResult(
    bool Success,
    string? Action = null,
    double Progress = 0.0,
    string? Error = null);
