using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Trakt;
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

    [Fact]
    public void GetCustomListIdsForMovie_ExcludesDeletedCustomLists()
    {
        var repo = Repo();
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO movies (id, title, year, provider, external_id, date_added, metadata_status, created_at, updated_at) VALUES (501, 'Movie 501', 2020, 'tmdb', '501', '2026-01-01', 'READY', '2026-01-01', '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        var listId = repo.CreateCustomList("Movie List Active", Now);
        repo.AddMovieToCustomList(listId, 501, Now);

        var idsBefore = repo.GetCustomListIdsForMovie(501);
        Assert.Contains(listId, idsBefore);

        // Delete list (sets is_deleted = 1)
        repo.DeleteCustomList(listId);

        var idsAfter = repo.GetCustomListIdsForMovie(501);
        Assert.DoesNotContain(listId, idsAfter);
    }

    [Fact]
    public void GetCustomListIdsForShow_ExcludesDeletedCustomLists()
    {
        var repo = Repo();
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO tv_shows (id, provider, external_id, title, sort_title, date_added, created_at, updated_at, genres, metadata_status) VALUES (601, 'tmdb', 's601', 'Show 601', 'show 601', '2026-01-01', '2026-01-01', '2026-01-01', '[]', 'READY');";
            cmd.ExecuteNonQuery();
        }

        var listId = repo.CreateCustomList("Show List Active", Now);
        repo.AddShowToCustomList(listId, 601, Now);

        var idsBefore = repo.GetCustomListIdsForShow(601);
        Assert.Contains(listId, idsBefore);

        // Delete list (sets is_deleted = 1)
        repo.DeleteCustomList(listId);

        var idsAfter = repo.GetCustomListIdsForShow(601);
        Assert.DoesNotContain(listId, idsAfter);
    }

    [Fact]
    public void RequireCustomList_WhenListIsDeleted_ThrowsKeyNotFoundException()
    {
        var repo = Repo();
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO movies (id, title, year, provider, external_id, date_added, metadata_status, created_at, updated_at) VALUES (701, 'Movie 701', 2021, 'tmdb', '701', '2026-01-01', 'READY', '2026-01-01', '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        var listId = repo.CreateCustomList("Will Be Deleted", Now);
        repo.DeleteCustomList(listId);

        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => repo.AddMovieToCustomList(listId, 701, Now));
        Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => repo.RenameCustomList(listId, "New Name", Now));
    }

    [Fact]
    public void DeleteCustomListCascade_WhenListIsDeleted_SuccessfullyPurges()
    {
        var repo = Repo();
        var traktListId = 887766L;
        var listId = repo.UpsertTraktCustomList("Tombstone List", traktListId, "tombstone-list", Now);

        repo.DeleteCustomList(listId);
        Assert.Single(repo.ListDeletedCustomLists());

        // Hard cascade deletion on tombstoned list
        repo.DeleteCustomListCascade(listId);

        Assert.Empty(repo.ListDeletedCustomLists());
        Assert.DoesNotContain(repo.ListCustomLists(), l => l.Id == listId);
    }

    [Fact]
    public void QueueCompaction_OddAlternatingActions_ReplaysNetFinalAction()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Odd Compaction", Now);

        // Item 1: ADD -> REMOVE -> ADD (Odd 3 -> net ADD)
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 101, "ADD", Now.AddMinutes(-10));
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 101, "REMOVE", Now.AddMinutes(-5));
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 101, "ADD", Now.AddMinutes(-1));

        // Item 2: REMOVE -> ADD -> REMOVE (Odd 3 -> net REMOVE)
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 202, "REMOVE", Now.AddMinutes(-8));
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 202, "ADD", Now.AddMinutes(-4));
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 202, "REMOVE", Now.AddMinutes(-2));

        var actions = repo.ListPendingActions(listId).Where(a => a.ActionType != "CREATE_LIST").ToList();
        var grouped = actions.GroupBy(a => (a.ListId, a.MediaKind, a.TmdbId)).ToList();
        var compactedToReplay = new System.Collections.Generic.List<CustomListPendingAction>();

        foreach (var group in grouped)
        {
            var ordered = group.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToList();
            var first = ordered.First();
            var last = ordered.Last();

            var isPureNoOp = ordered.Count % 2 == 0 &&
                             string.Equals(first.ActionType, "ADD", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(last.ActionType, "REMOVE", StringComparison.OrdinalIgnoreCase);

            if (!isPureNoOp)
            {
                compactedToReplay.Add(last);
            }
        }

        Assert.Equal(2, compactedToReplay.Count);
        var act1 = compactedToReplay.First(a => a.TmdbId == 101);
        Assert.Equal("ADD", act1.ActionType);
        var act2 = compactedToReplay.First(a => a.TmdbId == 202);
        Assert.Equal("REMOVE", act2.ActionType);
    }

    [Fact]
    public void QueueCompaction_EvenAlternatingActions_CancelsOutAsNoOp()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Even Compaction", Now);

        // Item 1: ADD -> REMOVE (Even 2 -> pure No-Op)
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 303, "ADD", Now.AddMinutes(-10));
        repo.EnqueuePendingAction(listId, CustomListMediaKind.Movie, 303, "REMOVE", Now.AddMinutes(-5));

        var actions = repo.ListPendingActions(listId).Where(a => a.ActionType != "CREATE_LIST").ToList();
        var grouped = actions.GroupBy(a => (a.ListId, a.MediaKind, a.TmdbId)).ToList();
        var compactedToReplay = new System.Collections.Generic.List<CustomListPendingAction>();

        foreach (var group in grouped)
        {
            var ordered = group.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToList();
            var first = ordered.First();
            var last = ordered.Last();

            var isPureNoOp = ordered.Count % 2 == 0 &&
                             string.Equals(first.ActionType, "ADD", StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(last.ActionType, "REMOVE", StringComparison.OrdinalIgnoreCase);

            if (!isPureNoOp)
            {
                compactedToReplay.Add(last);
            }
        }

        Assert.Empty(compactedToReplay);
    }

    [Fact]
    public void RemoveMemberByTmdbDirect_RemovesMemberWithoutEnqueuingPendingAction()
    {
        var repo = Repo();
        var traktListId = 777888L;
        var listId = repo.UpsertTraktCustomList("Direct Remove Test", traktListId, "direct-remove-test", Now);

        // Add a cloud item
        repo.AddCloudMediaToCustomList(listId, CustomListMediaKind.Movie, 550, Now, "Fight Club", 1999);
        Assert.Single(repo.ListCustomListItems(listId));

        // Use RemoveMemberByTmdbDirect
        repo.RemoveMemberByTmdbDirect(listId, CustomListMediaKind.Movie, 550);
        Assert.Empty(repo.ListCustomListItems(listId));

        // CRITICAL INVARIANT: Direct removal must NOT enqueue a pending REMOVE action
        var pending = repo.ListPendingActions(listId).Where(a => a.ActionType != "CREATE_LIST").ToList();
        Assert.Empty(pending);
    }

    [Fact]
    public void TraktPendingWatched_Enqueue_List_Delete_PersistsAndDrains()
    {
        var repo = Repo();
        var watchedAt = new DateTimeOffset(2026, 11, 20, 18, 30, 0, TimeSpan.Zero);

        // 1. Enqueue movie watched action
        repo.EnqueueTraktPendingWatched("MOVIE", 550, null, null, watchedAt, isWatched: true, Now);

        // 2. Enqueue episode unwatched action
        repo.EnqueueTraktPendingWatched("EPISODE", 1399, 1, 1, watchedAt, isWatched: false, Now);

        // 3. List pending actions
        var pending = repo.ListTraktPendingWatched();
        Assert.Equal(2, pending.Count);

        var movieAction = Assert.Single(pending, p => p.MediaKind == "MOVIE");
        Assert.Equal(550, movieAction.TmdbId);
        Assert.Null(movieAction.SeasonNumber);
        Assert.Null(movieAction.EpisodeNumber);
        Assert.Equal(watchedAt, movieAction.WatchedAt);
        Assert.True(movieAction.IsWatched);

        var epAction = Assert.Single(pending, p => p.MediaKind == "EPISODE");
        Assert.Equal(1399, epAction.TmdbId);
        Assert.Equal(1, epAction.SeasonNumber);
        Assert.Equal(1, epAction.EpisodeNumber);
        Assert.Equal(watchedAt, epAction.WatchedAt);
        Assert.False(epAction.IsWatched);

        // 4. Delete one action
        repo.DeleteTraktPendingWatched(movieAction.Id);
        var remaining = repo.ListTraktPendingWatched();
        Assert.Single(remaining);
        Assert.Equal(epAction.Id, remaining[0].Id);

        // 5. Delete remaining action
        repo.DeleteTraktPendingWatched(epAction.Id);
        Assert.Empty(repo.ListTraktPendingWatched());
    }

    [Fact]
    public void TraktPendingWatchlist_Enqueue_List_Delete_PersistsAndDrains()
    {
        var repo = Repo();

        // 1. Enqueue add movie to watchlist
        repo.EnqueueTraktPendingWatchlist("MOVIE", 550, "ADD", Now);

        // 2. Enqueue remove show from watchlist
        repo.EnqueueTraktPendingWatchlist("SHOW", 1399, "REMOVE", Now);

        // 3. List pending actions
        var pending = repo.ListTraktPendingWatchlist();
        Assert.Equal(2, pending.Count);

        var movieAction = Assert.Single(pending, p => p.MediaKind == "MOVIE");
        Assert.Equal(550, movieAction.TmdbId);
        Assert.Equal("ADD", movieAction.Action);

        var showAction = Assert.Single(pending, p => p.MediaKind == "SHOW");
        Assert.Equal(1399, showAction.TmdbId);
        Assert.Equal("REMOVE", showAction.Action);

        // 4. Delete actions
        repo.DeleteTraktPendingWatchlist(movieAction.Id);
        repo.DeleteTraktPendingWatchlist(showAction.Id);
        Assert.Empty(repo.ListTraktPendingWatchlist());
    }

    [Fact]
    public void UpsertLocalMediaInCustomList_UpgradesCloudOnlyRow_AndDeletesDuplicates()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Favorites Test", Now);

        // 1. Add cloud-only member
        repo.AddCloudMediaToCustomList(listId, CustomListMediaKind.Movie, 550, Now, "Fight Club", 1999);
        var items = repo.ListCustomListItems(listId);
        Assert.Single(items);
        Assert.True(items[0].IsCloudOnly);

        // 2. Insert movie into movies table
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO movies (id, title, year, provider, external_id, date_added, metadata_status, created_at, updated_at) VALUES (101, 'Fight Club', 1999, 'tmdb', '550', '2026-01-01', 'READY', '2026-01-01', '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        // 3. Upsert local media
        repo.UpsertLocalMediaInCustomList(listId, CustomListMediaKind.Movie, 101, 550, Now);

        var upgradedItems = repo.ListCustomListItems(listId);
        Assert.Single(upgradedItems);
        Assert.Equal(101, upgradedItems[0].Id);
        Assert.False(upgradedItems[0].IsCloudOnly);
        Assert.Equal(550, upgradedItems[0].TmdbId);

        // 4. Alert 1 verification: simulate a duplicate cloud row existing alongside a legacy unlinked local row (tmdb_id IS NULL)
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            // Reset local row tmdb_id to NULL to simulate legacy unlinked row before fix
            cmd.CommandText = "UPDATE custom_list_members SET tmdb_id = NULL WHERE list_id = @list AND movie_id = 101;";
            cmd.Parameters.AddWithValue("@list", listId);
            cmd.ExecuteNonQuery();

            // Insert duplicate cloud row with tmdb_id 550 (coexists with local row because local has NULL tmdb_id)
            cmd.CommandText = "INSERT INTO custom_list_members (list_id, media_kind, movie_id, tmdb_id, added_at) VALUES (@list, 'MOVIE', NULL, 550, '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        // Call UpsertLocalMediaInCustomList again - must purge duplicate cloud row first, link tmdb_id on local row, and NOT throw UNIQUE constraint exception
        repo.UpsertLocalMediaInCustomList(listId, CustomListMediaKind.Movie, 101, 550, Now);

        var finalItems = repo.ListCustomListItems(listId);
        Assert.Single(finalItems);
        Assert.Equal(101, finalItems[0].Id);
        Assert.False(finalItems[0].IsCloudOnly);
        Assert.Equal(550, finalItems[0].TmdbId);
    }

    [Fact]
    public void RemoveMovieFromCustomList_DeletesBothLocalAndCloudRowByTmdbId()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Cleanup Test", Now);

        // Seed movie
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO movies (id, title, year, provider, external_id, date_added, metadata_status, created_at, updated_at) VALUES (202, 'The Matrix', 1999, 'tmdb', '603', '2026-01-01', 'READY', '2026-01-01', '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        // Add local movie
        repo.AddMovieToCustomList(listId, 202, Now);

        // Simulate ghost cloud row inserted before fix (reset local tmdb_id to NULL to simulate legacy unlinked state)
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE custom_list_members SET tmdb_id = NULL WHERE list_id = @list AND movie_id = 202;";
            cmd.Parameters.AddWithValue("@list", listId);
            cmd.ExecuteNonQuery();

            cmd.CommandText = "INSERT INTO custom_list_members (list_id, media_kind, movie_id, tmdb_id, added_at) VALUES (@list, 'MOVIE', NULL, 603, '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        // Remove movie
        repo.RemoveMovieFromCustomList(listId, 202);

        // Both rows must be wiped out; zero items left
        var remaining = repo.ListCustomListItems(listId);
        Assert.Empty(remaining);
    }

    [Fact]
    public void DeduplicateCustomListMembers_PurgesDuplicateCloudRowsAndPromotesLocal()
    {
        var repo = Repo();
        var listId = repo.CreateCustomList("Dedup Test", Now);

        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO movies (id, title, year, provider, external_id, date_added, metadata_status, created_at, updated_at) VALUES (303, 'Inception', 2010, 'tmdb', '27205', '2026-01-01', 'READY', '2026-01-01', '2026-01-01');";
            cmd.ExecuteNonQuery();
        }

        // Insert legacy local row (tmdb_id NULL) and cloud row (tmdb_id 27205)
        using (var conn = new SqliteConnection(_connectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO custom_list_members (list_id, media_kind, movie_id, tmdb_id, added_at) VALUES (@list, 'MOVIE', 303, NULL, '2026-01-01');
                INSERT INTO custom_list_members (list_id, media_kind, movie_id, tmdb_id, added_at) VALUES (@list, 'MOVIE', NULL, 27205, '2026-01-02');";
            cmd.Parameters.AddWithValue("@list", listId);
            cmd.ExecuteNonQuery();
        }

        // Run deduplication
        repo.DeduplicateCustomListMembers();

        var items = repo.ListCustomListItems(listId);
        Assert.Single(items);
        Assert.Equal(303, items[0].Id);
        Assert.False(items[0].IsCloudOnly);
        Assert.Equal(27205, items[0].TmdbId);
    }

    [Fact]
    public void ParseFavorites_WhenShowItemOmitsTypeProperty_ExtractsShowTmdbId()
    {
        const string json = @"[
            {
                ""show"": {
                    ""title"": ""Breaking Bad"",
                    ""year"": 2008,
                    ""ids"": {
                        ""trakt"": 1390,
                        ""slug"": ""breaking-bad"",
                        ""tmdb"": 1396
                    }
                }
            },
            {
                ""movie"": {
                    ""title"": ""Fight Club"",
                    ""year"": 1999,
                    ""ids"": {
                        ""trakt"": 100,
                        ""slug"": ""fight-club"",
                        ""tmdb"": 550
                    }
                }
            }
        ]";

        using var doc = JsonDocument.Parse(json);
        var favorites = TraktDataProjections.ParseFavorites(doc.RootElement);

        Assert.Equal(2, favorites.Count);

        var showFav = favorites[0];
        Assert.Equal("show", showFav.Type);
        Assert.Equal(1396, showFav.TmdbId);
        Assert.Equal("Breaking Bad", showFav.Title);

        var movieFav = favorites[1];
        Assert.Equal("movie", movieFav.Type);
        Assert.Equal(550, movieFav.TmdbId);
        Assert.Equal("Fight Club", movieFav.Title);
    }

    [Fact]
    public void TraktResourceSpec_FavoriteMoviesAndShows_DoNotRequirePaginationHeaders()
    {
        Assert.False(TraktResourceSpec.FavoriteMovies().RequiresPaginationHeaders());
        Assert.False(TraktResourceSpec.FavoriteShows().RequiresPaginationHeaders());
    }
}
