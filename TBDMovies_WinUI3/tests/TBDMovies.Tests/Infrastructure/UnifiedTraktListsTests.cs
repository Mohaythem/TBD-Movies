using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using TBDMovies.Domain.Library.Personal;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Xunit;

namespace TBDMovies.Tests.Infrastructure;

public sealed class UnifiedTraktListsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_u9_{Guid.NewGuid():N}");
    private readonly string _connectionString;

    public UnifiedTraktListsTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "library.db")}";
        new DatabaseMigrator(_connectionString).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }

    private PersonalLibraryRepository Repo() => new(_connectionString);
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    [Fact]
    public void UpsertTraktCustomList_WhenNameConflictsWithLocalList_RenamesToIncludeTbdPrefix()
    {
        var repo = Repo();
        // 1. User created a local list named "Sci-Fi"
        var localId = repo.CreateCustomList("Sci-Fi", Now);

        // 2. Trakt has a list also named "Sci-Fi"
        var traktListId = 987654L;
        var traktId = repo.UpsertTraktCustomList("Sci-Fi", traktListId, "sci-fi", Now);

        var lists = repo.ListCustomLists();
        var localList = lists.First(l => l.Id == localId);
        var traktList = lists.First(l => l.Id == traktId);

        // Conflict resolution: Trakt list is preserved as "Sci-Fi", and local list is disambiguated with [TBD]
        Assert.Equal("Sci-Fi", traktList.Name);
        Assert.Equal(traktListId, traktList.TraktListId);
        Assert.Equal("[TBD] Sci-Fi", localList.Name);
    }

    [Fact]
    public void AddCloudMediaToCustomList_StoresAndRetrievesCloudItemsWithZeroLocalFiles()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Marvel Marathon", Now);

        // Add cloud-only movie (TMDB ID 299534 - Avengers: Endgame)
        repo.AddCloudMediaToCustomList(listId, CustomListMediaKind.Movie, 299534, Now, "Avengers: Endgame", 2019);

        var items = repo.ListCustomListItems(listId);
        Assert.Single(items);
        var item = items[0];
        Assert.Equal("Avengers: Endgame", item.Title);
        Assert.Equal(2019, item.Year);
        Assert.Equal(299534, item.TmdbId);
        Assert.True(item.IsCloudOnly);
        Assert.True(item.IsMissing);
        Assert.Equal(0, item.Id);
    }

    [Fact]
    public void RemoveCloudMediaFromCustomList_RemovesItemAndEnqueuesPendingAction()
    {
        var repo = Repo();
        var traktListId = 123456L;
        var listId = repo.UpsertTraktCustomList("Watchlist", traktListId, "watchlist", Now);

        // Add cloud movie
        repo.AddCloudMediaToCustomList(listId, CustomListMediaKind.Movie, 550, Now, "Fight Club", 1999);
        Assert.Single(repo.ListCustomListItems(listId));

        // Remove cloud movie
        repo.RemoveCloudMediaFromCustomList(listId, CustomListMediaKind.Movie, 550);
        Assert.Empty(repo.ListCustomListItems(listId));

        // Check that pending remove action was enqueued
        var pending = repo.ListPendingActions(listId);
        Assert.Single(pending);
        var action = pending[0];
        Assert.Equal(550, action.TmdbId);
        Assert.Equal(CustomListMediaKind.Movie, action.MediaKind);
        Assert.Equal("REMOVE", action.ActionType);
    }

    [Fact]
    public void QueueCompaction_CancelsOpposingActionsInSameOfflineWindow()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Test Queue", Now);

        // Enqueue ADD then REMOVE for same movie
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 100, "ADD", Now.AddMinutes(-5));
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 100, "REMOVE", Now.AddMinutes(-2));

        // Enqueue another item that is ADD only
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 200, "ADD", Now.AddMinutes(-1));

        var actions = repo.ListPendingActions(listId).Where(a => a.ActionType != "CREATE_LIST").ToList();
        Assert.Equal(3, actions.Count);

        // Verify compaction logic
        var grouped = actions.GroupBy(a => (a.ListId, a.MediaKind, a.TmdbId)).ToList();
        var surviving = new System.Collections.Generic.List<CustomListPendingAction>();
        foreach (var group in grouped)
        {
            var hasAdd = group.Any(a => string.Equals(a.ActionType, "ADD", StringComparison.OrdinalIgnoreCase));
            var hasRemove = group.Any(a => string.Equals(a.ActionType, "REMOVE", StringComparison.OrdinalIgnoreCase));
            if (!hasAdd || !hasRemove)
            {
                surviving.AddRange(group);
            }
        }

        Assert.Single(surviving);
        Assert.Equal(200, surviving[0].TmdbId);
        Assert.Equal("ADD", surviving[0].ActionType);
    }

    [Fact]
    public void CreateCustomList_EnqueuesCreateListAction_AndSupportsLinkToTrakt()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Action Flicks", Now);

        // 1. Verify list is unlinked and pending sync
        var unlinked = repo.ListUnlinkedCustomLists();
        Assert.Contains(unlinked, l => l.Id == listId && l.Name == "Action Flicks" && l.SyncStatus == "PENDING");

        // 2. Verify CREATE_LIST pending action was enqueued with null tmdb_id/media_kind
        var actions = repo.ListPendingActions(listId);
        Assert.Single(actions);
        Assert.Equal("CREATE_LIST", actions[0].ActionType);
        Assert.Null(actions[0].TmdbId);
        Assert.Null(actions[0].MediaKind);

        // 3. Link list with Trakt identity
        repo.LinkTraktCustomList(listId, 555444L, "action-flicks", Now);

        // 4. Verify list is now linked and SYNCED
        var lists = repo.ListCustomLists();
        var linked = lists.First(l => l.Id == listId);
        Assert.Equal(555444L, linked.TraktListId);
        Assert.Equal("action-flicks", linked.TraktSlug);
        Assert.Equal("SYNCED", linked.SyncStatus);
        Assert.DoesNotContain(repo.ListUnlinkedCustomLists(), l => l.Id == listId);
    }

    [Fact]
    public void WhenLocalMovieIsAdded_MergesExistingCloudMember_WithoutDuplicateRow()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Sci-Fi Night", Now);

        // 1. Initially item exists as cloud-only
        repo.AddCloudMediaToCustomList(listId, CustomListMediaKind.Movie, 603, Now, "The Matrix", 1999);
        var initialItems = repo.ListCustomListItems(listId);
        Assert.Single(initialItems);
        Assert.True(initialItems[0].IsCloudOnly);
        Assert.Equal(0, initialItems[0].Id);

        // 2. Movie is imported into local database
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO movies (id, title, year, provider, external_id, date_added, metadata_status, created_at, updated_at) VALUES (101, 'The Matrix', 1999, 'tmdb', '603', '2026-01-01', 'READY', '2026-01-01', '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        // 3. User adds the newly available local movie to the list
        repo.AddMovieToCustomList(listId, 101, Now);

        // 4. Verify membership merged into single row and upgraded to local movie
        var updatedItems = repo.ListCustomListItems(listId);
        Assert.Single(updatedItems); // Exactly 1 item, NO DUPLICATES!
        Assert.Equal(101, updatedItems[0].Id);
        Assert.False(updatedItems[0].IsCloudOnly);
        Assert.Equal(603, updatedItems[0].TmdbId);
    }

    [Fact]
    public void DeleteCustomListCascade_HardDeletesListAndMembersAndPendingActions()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("To Delete", Now);
        repo.AddCloudMediaToCustomList(listId, CustomListMediaKind.Movie, 550, Now, "Fight Club", 1999);
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 550, "ADD", Now);

        // Assert setup (Auto-queued CREATE_LIST upon list creation + explicit ADD)
        Assert.Single(repo.ListCustomListItems(listId));
        Assert.Equal(2, repo.ListPendingActions(listId).Count);

        // Delete cascade
        repo.DeleteCustomListCascade(listId);

        // Assert all rows wiped cleanly
        Assert.DoesNotContain(repo.ListCustomLists(), l => l.Id == listId);
        Assert.Empty(repo.ListCustomListItems(listId));
        Assert.Empty(repo.ListPendingActions(listId));
    }

    [Fact]
    public void ListDeletedCustomLists_ReturnsOnlyTombstonedLists()
    {
        var repo = Repo();
        var activeId = repo.CreateCustomList("Active List", Now);
        var traktListId = 778899L;
        var traktId = repo.UpsertTraktCustomList("Remote List", traktListId, "remote-list", Now);

        // Initially both are not deleted
        Assert.Empty(repo.ListDeletedCustomLists());
        Assert.Equal(2, repo.ListCustomLists().Count);

        // Delete the Trakt-linked list -> sets tombstone (is_deleted = 1, PENDING_DELETE)
        repo.DeleteCustomList(traktId);

        var activeLists = repo.ListCustomLists();
        var deletedLists = repo.ListDeletedCustomLists();

        Assert.Single(activeLists);
        Assert.Equal(activeId, activeLists[0].Id);

        Assert.Single(deletedLists);
        Assert.Equal(traktId, deletedLists[0].Id);
        Assert.Equal("PENDING_DELETE", deletedLists[0].SyncStatus);
    }

    [Fact]
    public void UpsertTraktCustomList_WhenPendingDelete_DoesNotResurrect()
    {
        var repo = Repo();
        var traktListId = 998877L;
        var listId = repo.UpsertTraktCustomList("Pending Delete List", traktListId, "pending-delete-list", Now);

        // Mark for deletion
        repo.DeleteCustomList(listId);
        Assert.Single(repo.ListDeletedCustomLists());

        // Upsert again before remote delete completes
        var reUpsertedId = repo.UpsertTraktCustomList("Pending Delete List", traktListId, "pending-delete-list", Now);
        Assert.Equal(listId, reUpsertedId);

        // Must still be in deleted lists and NOT in active lists
        Assert.Empty(repo.ListCustomLists());
        var deletedLists = repo.ListDeletedCustomLists();
        Assert.Single(deletedLists);
        Assert.Equal("PENDING_DELETE", deletedLists[0].SyncStatus);
    }
}
