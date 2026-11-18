using TBDMovies.Domain.Core.Operations;
using TBDMovies.Domain.Core.Safety;
using TBDMovies.FileSystem.Engine;
using TBDMovies.FileSystem.Operations;
using TBDMovies.FileSystem.Safety;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Xunit;

namespace TBDMovies.Tests.FileSystem;

public sealed class EngineTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public EngineTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_fs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "journal.db");
        _connectionString = $"Data Source={_databasePath}";
        new DatabaseMigrator(_connectionString).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void JournaledTransfer_UsesHardlinkAndCommits()
    {
        var source = CreateSource();
        var destination = Path.Combine(_directory, "destination.mkv");
        using var store = new FileOperationStore(_connectionString);
        using var coordinator = new FileOperationCoordinator(store);

        var (plan, _) = coordinator.Plan(OperationType.Move, source, destination, [_directory]);
        Assert.Equal(OperationState.Validated, store.GetById(plan.OperationId)!.State);
        var record = coordinator.Execute(plan.OperationId, [_directory]);

        Assert.Equal(OperationState.Committed, record.State);
        Assert.Equal("hardlink-unlink", record.Strategy);
        Assert.False(File.Exists(source));
        Assert.Equal("hello world", File.ReadAllText(destination));
    }

    [Fact]
    public void JournaledTransfer_ForcedCopyFlushesHashesAndCommits()
    {
        var source = CreateSource();
        var destination = Path.Combine(_directory, "destination.mkv");
        using var store = new FileOperationStore(_connectionString);
        using var engine = new SafeTransferEngine(forceCopy: true);
        using var coordinator = new FileOperationCoordinator(store, engine);

        var (plan, _) = coordinator.Plan(OperationType.Move, source, destination, [_directory]);
        var record = coordinator.Execute(plan.OperationId, [_directory]);

        Assert.Equal(OperationState.Committed, record.State);
        Assert.Equal("copy-sha256-flush-finalize-unlink", record.Strategy);
        Assert.Matches("^[0-9a-f]{64}$", record.DestinationSha256!);
        Assert.False(File.Exists(source));
        Assert.Equal("hello world", File.ReadAllText(destination));
    }

    [Fact]
    public void ExistingDestinationIsRejectedBeforeJournalCreation()
    {
        var source = CreateSource();
        var destination = Path.Combine(_directory, "destination.mkv");
        File.WriteAllText(destination, "existing");
        using var store = new FileOperationStore(_connectionString);
        using var coordinator = new FileOperationCoordinator(store);

        Assert.Throws<DestinationExistsException>(() =>
            coordinator.Plan(OperationType.Move, source, destination, [_directory]));
        Assert.True(File.Exists(source));
        Assert.Empty(store.ListHistory());
    }

    [Fact]
    public void SourceChangeAfterPreviewIsRejectedWithoutJournal()
    {
        var source = CreateSource();
        var destination = Path.Combine(_directory, "destination.mkv");
        using var store = new FileOperationStore(_connectionString);
        using var coordinator = new FileOperationCoordinator(store);
        var preview = coordinator.Preview(OperationType.Move, source, destination, [_directory]);
        File.AppendAllText(source, "changed");

        Assert.Throws<SourceChangedException>(() =>
            coordinator.Plan(
                OperationType.Move, source, destination, [_directory],
                expectedSourceIdentity: preview.SourceIdentity));
        Assert.Empty(store.ListHistory());
    }

    [Fact]
    public void PathOutsideApprovedRootIsRejected()
    {
        var source = CreateSource();
        var other = Path.Combine(Path.GetTempPath(), $"tbdmovies_outside_{Guid.NewGuid():N}");
        Directory.CreateDirectory(other);
        try
        {
            using var store = new FileOperationStore(_connectionString);
            using var coordinator = new FileOperationCoordinator(store);
            Assert.Throws<UnsafePathException>(() =>
                coordinator.Plan(OperationType.Move, source, Path.Combine(other, "destination.mkv"), [_directory]));
        }
        finally
        {
            Directory.Delete(other, true);
        }
    }

    [Fact]
    public void HardlinkFailure_CleansUpOrphanedDestinationAndPreservesSource()
    {
        var source = CreateSource();
        var destination = Path.Combine(_directory, "orphaned_dest.mkv");
        var expected = WindowsNative.Identity(source);
        using var engine = new SafeTransferEngine(forceCopy: false)
        {
            DestinationIdentityResolverForTesting = id => new SourceIdentity(id.Size + 1, id.MTimeNs, id.Dev, id.Ino)
        };

        var ex = Assert.Throws<IOException>(() =>
            engine.Prepare(source, destination, expected, "op_test_hl_fail"));
        Assert.Contains("Hard-link destination does not identify the locked source", ex.Message);
        Assert.False(File.Exists(destination), "Orphaned hardlink destination must be deleted on verification failure.");
        Assert.True(File.Exists(source), "Source file must remain intact and unaffected.");
        Assert.Equal("hello world", File.ReadAllText(source));
    }

    [Fact]
    public void ForcedCopyFailure_CleansUpOrphanedDestinationAndPreservesSource()
    {
        var source = CreateSource();
        var destination = Path.Combine(_directory, "orphaned_dest_copy.mkv");
        var expected = WindowsNative.Identity(source);
        using var engine = new SafeTransferEngine(forceCopy: true)
        {
            DestinationHashResolverForTesting = hash => "0000000000000000000000000000000000000000000000000000000000000000"
        };

        var ex = Assert.Throws<IOException>(() =>
            engine.Prepare(source, destination, expected, "op_test_copy_fail"));
        Assert.Contains("Destination SHA-256 verification failed", ex.Message);
        Assert.False(File.Exists(destination), "Destination copy must be deleted on SHA-256 verification failure.");
        Assert.True(File.Exists(source), "Source file must remain intact and unaffected.");
        Assert.Equal("hello world", File.ReadAllText(source));
    }

    private string CreateSource()
    {
        var source = Path.Combine(_directory, "source.mkv");
        File.WriteAllText(source, "hello world");
        return source;
    }
}
