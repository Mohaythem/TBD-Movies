using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TBDMovies.FileSystem.Inspection;

/// <summary>Bounds synchronous filesystem calls against a network share.</summary>
public sealed class NetworkStorageUnavailableException : IOException
{
    public NetworkStorageUnavailableException(string path)
        : base($"Network storage is unavailable: {path}") { }
}

public sealed class NetworkReadGuard
{
    public static NetworkReadGuard Shared { get; } = new();

    private sealed class ShareState
    {
        public SemaphoreSlim Semaphore { get; }
        public ConcurrentDictionary<Task, byte> HungTasks { get; } = new();

        public ShareState(int maxConcurrency)
        {
            Semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        }
    }

    private readonly TimeSpan _timeout;
    private readonly int _maxConcurrencyPerShare;
    private readonly Func<char, DriveType>? _driveTypeProvider;
    private readonly ConcurrentDictionary<string, ShareState> _shares = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<char, (bool IsNetwork, long ExpiryTicks)> _driveTypeCache = new();
    private static readonly TimeSpan DriveCacheTtl = TimeSpan.FromSeconds(15);

    public NetworkReadGuard(
        TimeSpan? timeout = null,
        int maxConcurrencyPerShare = 4,
        Func<char, DriveType>? driveTypeProvider = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(2);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maxConcurrencyPerShare <= 0) throw new ArgumentOutOfRangeException(nameof(maxConcurrencyPerShare));
        _maxConcurrencyPerShare = maxConcurrencyPerShare;
        _driveTypeProvider = driveTypeProvider;
    }

    public T Execute<T>(string path, Func<T> read, Func<T> unavailable, Func<bool>? isCancelled = null)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(unavailable);
        if (isCancelled?.Invoke() == true) throw new OperationCanceledException();

        if (!IsNetworkPath(path, _driveTypeProvider))
        {
            var local = read();
            if (isCancelled?.Invoke() == true) throw new OperationCanceledException();
            return local;
        }

        var share = ShareKey(path);
        var shareState = _shares.GetOrAdd(share, _ => new ShareState(_maxConcurrencyPerShare));

        // If this share currently has any hung tasks, fail fast to protect the thread pool
        if (!shareState.HungTasks.IsEmpty)
        {
            return unavailable();
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var timeoutTicks = (long)(_timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

        var acquired = false;
        while (!acquired)
        {
            if (!shareState.HungTasks.IsEmpty)
            {
                return unavailable();
            }

            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            if (elapsed >= timeoutTicks)
            {
                return unavailable();
            }

            if (isCancelled?.Invoke() == true) throw new OperationCanceledException();

            var remainingMs = Math.Max(1, (int)((timeoutTicks - elapsed) * 1000L / System.Diagnostics.Stopwatch.Frequency));
            var sliceMs = Math.Min(50, remainingMs);

            acquired = shareState.Semaphore.Wait(sliceMs);
        }

        var slotReleased = false;
        Task<T>? task = null;
        try
        {
            if (isCancelled?.Invoke() == true) throw new OperationCanceledException();

            task = Task.Run(read);

            while (!task.IsCompleted)
            {
                var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - started;
                if (elapsed >= timeoutTicks)
                {
                    shareState.HungTasks[task] = 0;

                    _ = task.ContinueWith(t =>
                    {
                        _ = t.Exception;
                        shareState.HungTasks.TryRemove(t, out _);
                        shareState.Semaphore.Release();
                    }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

                    slotReleased = true;
                    return unavailable();
                }

                if (isCancelled?.Invoke() == true) throw new OperationCanceledException();

                var remainingMs = Math.Max(1, Math.Min(50, (int)((timeoutTicks - elapsed) * 1000L / System.Diagnostics.Stopwatch.Frequency)));
                try
                {
                    task.Wait(remainingMs);
                }
                catch (AggregateException) when (task.IsCompleted)
                {
                    break;
                }
            }

            if (isCancelled?.Invoke() == true) throw new OperationCanceledException();

            return task.GetAwaiter().GetResult();
        }
        finally
        {
            if (!slotReleased)
            {
                shareState.Semaphore.Release();
            }
        }
    }

    public bool CheckNetworkPath(string path) => IsNetworkPath(path, _driveTypeProvider);

    public static bool IsNetworkPath(string path) => IsNetworkPath(path, driveTypeProvider: null);

    public static bool IsNetworkPath(string path, Func<char, DriveType>? driveTypeProvider)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        if ((path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !path.StartsWith(@"\\.\", StringComparison.Ordinal)) ||
            path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryExtractDriveLetter(path, out var driveLetter))
        {
            return IsNetworkDrive(driveLetter, driveTypeProvider);
        }

        return false;
    }

    public static bool TryExtractDriveLetter(string path, out char driveLetter)
    {
        driveLetter = default;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var trimmed = path.Trim();
        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            driveLetter = char.ToUpperInvariant(trimmed[0]);
            return true;
        }

        if (trimmed.Length >= 6 &&
            (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) || trimmed.StartsWith(@"\\.\", StringComparison.Ordinal)) &&
            char.IsLetter(trimmed[4]) && trimmed[5] == ':')
        {
            driveLetter = char.ToUpperInvariant(trimmed[4]);
            return true;
        }

        return false;
    }

    private static bool IsNetworkDrive(char driveLetter, Func<char, DriveType>? provider)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_driveTypeCache.TryGetValue(driveLetter, out var cached) && now < cached.ExpiryTicks)
        {
            return cached.IsNetwork;
        }

        var isNetwork = false;
        try
        {
            if (provider is not null)
            {
                isNetwork = provider(driveLetter) == DriveType.Network;
            }
            else if (OperatingSystem.IsWindows())
            {
                var drive = new DriveInfo(driveLetter.ToString());
                isNetwork = drive.DriveType == DriveType.Network;
            }
        }
        catch
        {
            isNetwork = false;
        }

        var expiry = now + (long)(DriveCacheTtl.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
        _driveTypeCache[driveLetter] = (isNetwork, expiry);
        return isNetwork;
    }

    public static string ShareKey(string path)
    {
        if (TryExtractDriveLetter(path, out var driveLetter))
        {
            return $"{char.ToUpperInvariant(driveLetter)}:\\";
        }

        var normalized = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..]
            : path;
        var parts = normalized.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}".ToUpperInvariant() : normalized.ToUpperInvariant();
    }
}
