using System;
using System.Collections.Immutable;
using System.IO;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

public sealed class CatalogSavepointTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_savepoint_{Guid.NewGuid():N}");
    private readonly string _connectionString;

    public CatalogSavepointTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "library.db")}";
        new DatabaseMigrator(_connectionString).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch { }
    }

    [Theory]
    [InlineData("sp_0")]
    [InlineData("_savepoint")]
    [InlineData("SP_BATCH_123")]
    [InlineData("SavepointAlpha")]
    [InlineData("a")]
    [InlineData("_")]
    public void Savepoint_ValidIdentifiers_ExecuteSuccessfully(string validName)
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using var uow = factory.Begin();

        uow.Savepoint(validName);
        uow.ReleaseSavepoint(validName);
        uow.Commit();
    }

    [Fact]
    public void Savepoint_Max64CharsIdentifier_Succeeds()
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using var uow = factory.Begin();

        var maxName = "s" + new string('a', 63); // 64 chars total, starts with letter
        Assert.Equal(64, maxName.Length);

        uow.Savepoint(maxName);
        uow.ReleaseSavepoint(maxName);
        uow.Commit();
    }

    [Fact]
    public void Savepoint_Rollback_RevertsChangesWithinSavepoint()
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using (var uow = factory.Begin())
        {
            // Insert movie 1 outside savepoint
            uow.Movies.Create(new MovieCatalogData(
                "tmdb", "id_1", "Heat", null, 1995, null,
                ImmutableArray<string>.Empty, null, null, null,
                MetadataStatus.NeedsMatch, null, null), DateTimeOffset.UtcNow);

            // Start savepoint and insert movie 2
            uow.Savepoint("sp_test");
            uow.Movies.Create(new MovieCatalogData(
                "tmdb", "id_2", "Casino", null, 1995, null,
                ImmutableArray<string>.Empty, null, null, null,
                MetadataStatus.NeedsMatch, null, null), DateTimeOffset.UtcNow);

            // Rollback movie 2
            uow.RollbackToSavepoint("sp_test");

            uow.Commit();
        }

        // Verify only movie 1 was persisted
        using (var uow = factory.BeginReadOnly())
        {
            var m1 = uow.Movies.GetByExternalId("tmdb", "id_1");
            var m2 = uow.Movies.GetByExternalId("tmdb", "id_2");

            Assert.NotNull(m1);
            Assert.Equal("Heat", m1.Title);
            Assert.Null(m2);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1_leading_digit")]
    [InlineData("123")]
    [InlineData("sp 1")]
    [InlineData("sp-1")]
    [InlineData("sp.1")]
    [InlineData("sp\"; DROP TABLE movies; --")]
    [InlineData("sp'test")]
    [InlineData("sp\ntest")]
    [InlineData("sp\0test")]
    public void Savepoint_InvalidNames_ThrowArgumentException(string? invalidName)
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using var uow = factory.Begin();

        Assert.ThrowsAny<ArgumentException>(() => uow.Savepoint(invalidName!));
        Assert.ThrowsAny<ArgumentException>(() => uow.ReleaseSavepoint(invalidName!));
        Assert.ThrowsAny<ArgumentException>(() => uow.RollbackToSavepoint(invalidName!));
    }

    [Fact]
    public void Savepoint_NameExceeding64Chars_ThrowsArgumentException()
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using var uow = factory.Begin();

        var tooLong = "s" + new string('a', 64); // 65 chars
        Assert.Equal(65, tooLong.Length);

        Assert.Throws<ArgumentException>(() => uow.Savepoint(tooLong));
        Assert.Throws<ArgumentException>(() => uow.ReleaseSavepoint(tooLong));
        Assert.Throws<ArgumentException>(() => uow.RollbackToSavepoint(tooLong));
    }

    [Fact]
    public void Savepoint_AfterCommit_ThrowsInvalidOperationException()
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using var uow = factory.Begin();
        uow.Commit();

        Assert.Throws<InvalidOperationException>(() => uow.Savepoint("sp_1"));
        Assert.Throws<InvalidOperationException>(() => uow.ReleaseSavepoint("sp_1"));
        Assert.Throws<InvalidOperationException>(() => uow.RollbackToSavepoint("sp_1"));
    }

    [Fact]
    public void Savepoint_AfterDispose_ThrowsObjectDisposedException()
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        var uow = factory.Begin();
        uow.Dispose();

        Assert.Throws<ObjectDisposedException>(() => uow.Savepoint("sp_1"));
        Assert.Throws<ObjectDisposedException>(() => uow.ReleaseSavepoint("sp_1"));
        Assert.Throws<ObjectDisposedException>(() => uow.RollbackToSavepoint("sp_1"));
    }

    [Fact]
    public void Savepoint_ReadOnly_IsSafeNoOp()
    {
        var factory = new CatalogUnitOfWorkFactory(_connectionString);
        using var uow = factory.BeginReadOnly();

        // Should return cleanly without throwing
        uow.Savepoint("sp_read_only");
        uow.ReleaseSavepoint("sp_read_only");
        uow.RollbackToSavepoint("sp_read_only");
    }
}
