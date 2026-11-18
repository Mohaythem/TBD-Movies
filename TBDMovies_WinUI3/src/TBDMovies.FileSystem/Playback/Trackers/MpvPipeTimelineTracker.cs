using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.FileSystem.Playback.Trackers;

public sealed class MpvPipeTimelineTracker : IPlayerTimelineTracker
{
    private readonly string _pipeName;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;

    public MpvPipeTimelineTracker(string pipeName)
    {
        _pipeName = pipeName;
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return false;
        if (_pipe is { IsConnected: true }) return true;

        try
        {
            _pipe?.Dispose();
            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMilliseconds(500));

            await _pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            _reader = new StreamReader(_pipe, Encoding.UTF8);
            _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PlayerTimelineSnapshot?> PollAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        try
        {
            if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            // Query time-pos
            await _writer!.WriteLineAsync("{\"command\":[\"get_property\",\"time-pos\"]}".AsMemory(), cancellationToken).ConfigureAwait(false);
            var timeLine = await _reader!.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(timeLine)) return null;

            var timePos = ExtractDoubleProperty(timeLine);
            if (!timePos.HasValue) return null;

            // Query pause
            await _writer.WriteLineAsync("{\"command\":[\"get_property\",\"pause\"]}".AsMemory(), cancellationToken).ConfigureAwait(false);
            var pauseLine = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var isPaused = ExtractBoolProperty(pauseLine) ?? false;

            // Query duration
            await _writer.WriteLineAsync("{\"command\":[\"get_property\",\"duration\"]}".AsMemory(), cancellationToken).ConfigureAwait(false);
            var durLine = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var duration = ExtractDoubleProperty(durLine);

            return new PlayerTimelineSnapshot(
                PositionSeconds: Math.Max(0, timePos.Value),
                DurationSeconds: duration,
                IsPaused: isPaused);
        }
        catch
        {
            return null;
        }
    }

    public static double? ExtractDoubleProperty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
            {
                return data.GetDouble();
            }
        }
        catch { }
        return null;
    }

    public static bool? ExtractBoolProperty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && (data.ValueKind == JsonValueKind.True || data.ValueKind == JsonValueKind.False))
            {
                return data.GetBoolean();
            }
        }
        catch { }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        try
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _pipe?.Dispose();
        }
        catch { }
        return ValueTask.CompletedTask;
    }
}
