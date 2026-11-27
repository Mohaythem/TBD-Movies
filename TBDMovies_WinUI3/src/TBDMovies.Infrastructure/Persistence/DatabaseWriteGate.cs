using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace TBDMovies.Infrastructure.Persistence;

/// <summary>
/// Serialises every catalog and personal-library write against a given database file inside this
/// process. SQLite permits one writer at a time; TBDMovies opens catalog work and personal-library
/// work on independent connections that each take an IMMEDIATE (write-lock) transaction, so the two
/// competed for the single writer slot. SQLite's own busy handling is unfair and its wait is bounded
/// by Microsoft.Data.Sqlite's command timeout rather than by <c>PRAGMA busy_timeout</c>, so a burst
/// of catalog writes could starve a "Mark Watched" write until it failed with "database is locked".
/// A fair in-process gate keyed by the database path removes that contention entirely: writers queue
/// here instead of colliding at the SQLite layer. Reads that do not take the write lock are not gated.
/// </summary>
internal static class DatabaseWriteGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

#if DEBUG
    // Developer tripwire (SEC-I2): acquisition is documented as non-reentrant and TBDMovies never
    // nests one gated scope inside another, so a thread that acquires while already holding a gate
    // is a latent self-deadlock or cross-gate deadlock. Debug builds fail fast on it; Release
    // builds compile this tracking away entirely (zero overhead on the hot path).
    // Known blind spot: async re-entrancy across thread-pool threads is not observed by a
    // thread-local; the synchronous call-site contract is what this guards.
    private static readonly ThreadLocal<int> DebugHeldScopes = new();
#endif

    /// <summary>
    /// Blocks until this process's writer slot for <paramref name="connectionString" />'s database is
    /// free, then returns a token that releases it on <see cref="IDisposable.Dispose" />. Acquisition
    /// is not re-entrant: a thread that already holds the gate must not acquire it again (TBDMovies never
    /// nests a catalog unit of work or a personal write inside another gated scope).
    /// </summary>
    public static IDisposable Enter(string connectionString)
    {
        var gate = Gates.GetOrAdd(KeyFor(connectionString), _ => new SemaphoreSlim(1, 1));

#if DEBUG
        if (DebugHeldScopes.Value > 0)
        {
            throw new InvalidOperationException(
                "DatabaseWriteGate.Enter was acquired re-entrantly on this thread. Gated scopes are not nestable; see DatabaseWriteGate doc comment.");
        }

        DebugHeldScopes.Value = 1;
        try
        {
            gate.Wait();
        }
        catch
        {
            DebugHeldScopes.Value = 0;
            throw;
        }
#else
        gate.Wait();
#endif

        return new Releaser(gate);
    }

    private static string KeyFor(string connectionString)
    {
        try
        {
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            if (string.IsNullOrWhiteSpace(dataSource)) return connectionString;
            if (dataSource == ":memory:" || dataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
                return connectionString;
            return Path.GetFullPath(dataSource).ToLowerInvariant();
        }
        catch
        {
            return connectionString;
        }
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;
        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
#if DEBUG
            DebugHeldScopes.Value = 0;
#endif
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }
}
