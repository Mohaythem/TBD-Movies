using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.FileSystem.Playback.Trackers;

public sealed class VlcHttpTimelineTracker : IPlayerTimelineTracker
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromMilliseconds(800)
    };

    private readonly string _url;
    private readonly AuthenticationHeaderValue _authHeader;
    private bool _disposed;

    public VlcHttpTimelineTracker(int port = 8088, string password = "tbd_internal")
    {
        _url = $"http://127.0.0.1:{port}/requests/status.json";
        var rawAuth = $":{password}";
        _authHeader = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(rawAuth)));
    }

    public async Task<PlayerTimelineSnapshot?> PollAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(800));

            using var request = new HttpRequestMessage(HttpMethod.Get, _url);
            request.Headers.Authorization = _authHeader;

            using var response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseStatusJson(json);
        }
        catch
        {
            return null;
        }
    }

    public static PlayerTimelineSnapshot? ParseStatusJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("time", out var timeProp)) return null;
            var posSeconds = timeProp.GetDouble();

            double? durationSeconds = null;
            if (root.TryGetProperty("length", out var lenProp) && lenProp.GetDouble() > 0)
            {
                durationSeconds = lenProp.GetDouble();
            }

            var isPaused = false;
            if (root.TryGetProperty("state", out var stateProp))
            {
                var state = stateProp.GetString();
                isPaused = string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase);
            }

            return new PlayerTimelineSnapshot(
                PositionSeconds: Math.Max(0, posSeconds),
                DurationSeconds: durationSeconds,
                IsPaused: isPaused);
        }
        catch
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
