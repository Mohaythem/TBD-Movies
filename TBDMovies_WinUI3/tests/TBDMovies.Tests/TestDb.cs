using Microsoft.Data.Sqlite;

namespace TBDMovies.Tests;

/// <summary>
/// Shared connection factory for tests that write through their own connections.
/// CI invariant VIII: every test connection executes <c>PRAGMA foreign_keys = ON;</c>
/// so orphaned seeds fail loudly instead of being silently accepted. The pragma is
/// per-connection and does not persist across connections or migrations, which is why
/// every freshly opened connection must set it again.
/// </summary>
public static class TestDb
{
    public static SqliteConnection OpenForeignKeysOn(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
