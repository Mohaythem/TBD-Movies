using Microsoft.Data.Sqlite;

namespace TBDMovies.Infrastructure.Persistence;

internal static class SqliteConnections
{
    internal static SqliteConnection Open(string connectionString)
    {
        var isReadOnly = false;
        try
        {
            isReadOnly = new SqliteConnectionStringBuilder(connectionString).Mode == SqliteOpenMode.ReadOnly;
        }
        catch
        {
            // Fallback to standard pragma block if connection string is non-standard.
        }

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = isReadOnly
            ? """
              PRAGMA foreign_keys = ON;
              PRAGMA secure_delete = ON;
              PRAGMA busy_timeout = 10000;
              PRAGMA cache_size = -64000;
              PRAGMA mmap_size = 268435456;
              PRAGMA temp_store = MEMORY;
              """
            : """
              PRAGMA foreign_keys = ON;
              PRAGMA journal_mode = WAL;
              PRAGMA synchronous = NORMAL;
              PRAGMA secure_delete = ON;
              PRAGMA busy_timeout = 10000;
              PRAGMA cache_size = -64000;
              PRAGMA mmap_size = 268435456;
              PRAGMA temp_store = MEMORY;
              """;
        command.ExecuteNonQuery();
        return connection;
    }
}
