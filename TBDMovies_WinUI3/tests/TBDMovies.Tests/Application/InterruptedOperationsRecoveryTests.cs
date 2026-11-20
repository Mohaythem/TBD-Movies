using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Domain.Core.Operations;
using TBDMovies.FileSystem.Operations;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class InterruptedOperationsRecoveryTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _allowedDir;
    private readonly string _unauthorizedDir;
    private readonly string _connectionString;

    public InterruptedOperationsRecoveryTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), $"tbdmovies_interrupted_test_{Guid.NewGuid():N}");
        _allowedDir = Path.Combine(_workspace, "AllowedMedia");
        _unauthorizedDir = Path.Combine(_workspace, "UnauthorizedDir");
        Directory.CreateDirectory(_allowedDir);
        Directory.CreateDirectory(_unauthorizedDir);

        _connectionString = $"Data Source={Path.Combine(_workspace, "library.db")}";
        new DatabaseMigrator(_connectionString).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch { }
    }

    private sealed class MemorySettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _dict = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _dict.TryGetValue(key, out var val) ? val : null;
        public void Set(string key, string value, DateTimeOffset updatedAt) => _dict[key] = value;
        public void Delete(string key) => _dict.Remove(key);
    }

    [Fact]
    public void InspectRecovery_With_Empty_Approved_Roots_Returns_UnsafeDestination_Without_Throwing()
    {
        using var store = new FileOperationStore(_connectionString);
        using var coordinator = new FileOperationCoordinator(store);

        var source = Path.Combine(_allowedDir, "sample.mkv");
        var destination = Path.Combine(_allowedDir, "dest.mkv");
        File.WriteAllText(source, "payload");

        var plan = coordinator.Plan(OperationType.Move, source, destination, [_allowedDir]).Plan;
        store.Transition(plan.OperationId, OperationState.Executing);
        File.Move(source, destination);

        // Call InspectRecovery with an empty approved roots list (e.g. unconfigured provider)
        var inspection = coordinator.InspectRecovery(plan.OperationId, Array.Empty<string>());

        Assert.Equal(RecoverySituation.DestinationUnsafeOrChanged, inspection.Situation);
        Assert.False(inspection.CanReconcile);
    }

    [Fact]
    public void Recover_With_Destination_Outside_Approved_Roots_Transitions_To_RecoveryRequired()
    {
        using var store = new FileOperationStore(_connectionString);
        using var coordinator = new FileOperationCoordinator(store);

        var source = Path.Combine(_allowedDir, "sample.mkv");
        var destination = Path.Combine(_unauthorizedDir, "dest.mkv");
        File.WriteAllText(source, "payload");

        var plan = coordinator.Plan(OperationType.Move, source, destination, [_allowedDir, _unauthorizedDir]).Plan;
        store.Transition(plan.OperationId, OperationState.Executing);
        File.Move(source, destination);

        // When recovery runs only with _allowedDir as approved, destination is in _unauthorizedDir
        var recovered = coordinator.Recover(plan.OperationId, [_allowedDir]);

        Assert.Equal(OperationState.RecoveryRequired, recovered.State);
    }

    [Fact]
    public void OperationHistoryService_Uses_ApprovedRootsProvider_And_Rejects_Unapproved_Paths()
    {
        using var store = new FileOperationStore(_connectionString);
        using var coordinator = new FileOperationCoordinator(store);

        var source = Path.Combine(_allowedDir, "sample.mkv");
        var destination = Path.Combine(_unauthorizedDir, "dest.mkv");
        File.WriteAllText(source, "payload");

        var plan = coordinator.Plan(OperationType.Move, source, destination, [_allowedDir, _unauthorizedDir]).Plan;
        store.Transition(plan.OperationId, OperationState.Executing);
        File.Move(source, destination);

        // Inject provider that only knows _allowedDir
        var history = new OperationHistoryService(
            store,
            coordinator,
            mediaFiles: null,
            movies: null,
            tracker: null,
            approvedRootsProvider: () => [_allowedDir]);

        var assessment = history.InspectRecovery(plan.OperationId);
        Assert.Equal(RecoverySituation.DestinationUnsafeOrChanged, assessment.Situation);

        var result = history.AttemptRecovery(plan.OperationId);
        Assert.Equal(OperationState.RecoveryRequired, result.ResultState);
    }

    [Fact]
    public void ApprovedRoots_Includes_Ui_Media_Folders_Enabling_Legitimate_Recovery()
    {
        using var store = new FileOperationStore(_connectionString);
        using var coordinator = new FileOperationCoordinator(store);

        var source = Path.Combine(_allowedDir, "legit.mkv");
        var destination = Path.Combine(_allowedDir, "dest_legit.mkv");
        File.WriteAllText(source, "video_content");

        var plan = coordinator.Plan(OperationType.Move, source, destination, [_allowedDir]).Plan;
        store.Transition(plan.OperationId, OperationState.Executing);

        // Settings has _allowedDir in ui_media_folders
        var settings = new MemorySettingsRepository();
        settings.Set("ui_media_folders", _allowedDir, DateTimeOffset.UtcNow);

        var approvedRoots = ApprovedRootsReader.ResolveApprovedRoots(settings, _workspace, skipNetworkPaths: true, verifyDirectoryExists: true);
        Assert.Contains(_allowedDir, approvedRoots, StringComparer.OrdinalIgnoreCase);

        // Execute recovery with the resolved approved roots; since destination does not exist and source does,
        // it transitions safely to Failed (source preserved)
        var recovered = coordinator.Recover(plan.OperationId, approvedRoots);
        Assert.Equal(OperationState.Failed, recovered.State);
        Assert.True(File.Exists(source));
    }
}
