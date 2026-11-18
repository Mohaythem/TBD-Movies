using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using TBDMovies.Application.Contracts;
using TBDMovies.Application.Dto;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Domain.Library.Movies;
using TBDMovies.Domain.Library.Shows;
using TBDMovies.Infrastructure.Persistence.Migrations;
using TBDMovies.Infrastructure.Persistence.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace TBDMovies.Tests.Application;

public sealed class TvContinueWatchingOptimizationTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public TvContinueWatchingOptimizationTests(ITestOutputHelper output)
    {
        _output = output;
        _directory = Path.Combine(Path.GetTempPath(), $"tbdmovies_cw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "library.db");
        _connectionString = $"Data Source={_databasePath}";
        new DatabaseMigrator(_connectionString).Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void FastPathSql_And_InMemoryFallback_ProduceIdenticalContinueWatchingResults()
    {
        int show1Id, show2Id, show3Id, show4Id;

        using (var unit = new CatalogUnitOfWork(_connectionString))
        {
            // Show 1: Partially watched (Ep 1 watched, Ep 2 present, Ep 3 present) -> next is Ep 2
            var s1 = unit.TvShows.Create(new TvShowCatalogData("tmdb", "s1", "Breaking Bad", "breaking bad", 2008, "Overview", [], "poster1.jpg", MetadataStatus.Ready, backdropReference: "backdrop1.jpg"), Now.AddMinutes(-10));
            show1Id = s1.Id;
            unit.TvShows.SetCustomDisplayTitle(show1Id, "BrBa Custom");
            var s1Season = unit.TvSeasons.Create(show1Id, 1, "Season 1", null, Now);
            var s1Ep1 = unit.TvEpisodes.Create(s1Season.Id, 1, "Pilot", null, null, null, Now);
            var s1Ep2 = unit.TvEpisodes.Create(s1Season.Id, 2, "Cat's in the Bag", null, null, null, Now);
            var s1Ep3 = unit.TvEpisodes.Create(s1Season.Id, 3, "...And the Bag's in the River", null, null, null, Now);

            var f1_1 = unit.MediaFiles.Add(new VerifiedMediaFileFacts(Path.Combine(_directory, "BB.S01E01.mkv"), 1024, ".mkv", "1080p", "H264", "WEB", Now));
            var f1_2 = unit.MediaFiles.Add(new VerifiedMediaFileFacts(Path.Combine(_directory, "BB.S01E02.mkv"), 1024, ".mkv", "1080p", "H264", "WEB", Now));
            var f1_3 = unit.MediaFiles.Add(new VerifiedMediaFileFacts(Path.Combine(_directory, "BB.S01E03.mkv"), 1024, ".mkv", "1080p", "H264", "WEB", Now));
            unit.TvEpisodes.LinkMediaFile(s1Ep1.Id, f1_1.Id, Now);
            unit.TvEpisodes.LinkMediaFile(s1Ep2.Id, f1_2.Id, Now);
            unit.TvEpisodes.LinkMediaFile(s1Ep3.Id, f1_3.Id, Now);

            // Show 2: Partially watched, but Ep 2 is missing, Ep 3 is present -> next is Ep 3
            var s2 = unit.TvShows.Create(new TvShowCatalogData("tmdb", "s2", "Better Call Saul", "better call saul", 2015, "Overview", [], "poster2.jpg", MetadataStatus.Ready, backdropReference: "backdrop2.jpg"), Now.AddMinutes(-5));
            show2Id = s2.Id;
            var s2Season = unit.TvSeasons.Create(show2Id, 1, "Season 1", null, Now);
            var s2Ep1 = unit.TvEpisodes.Create(s2Season.Id, 1, "Uno", null, null, null, Now);
            var s2Ep2 = unit.TvEpisodes.Create(s2Season.Id, 2, "Mijo", null, null, null, Now);
            var s2Ep3 = unit.TvEpisodes.Create(s2Season.Id, 3, "Nacho", null, null, null, Now);

            var f2_1 = unit.MediaFiles.Add(new VerifiedMediaFileFacts(Path.Combine(_directory, "BCS.S01E01.mkv"), 1024, ".mkv", "1080p", "H264", "WEB", Now));
            var f2_3 = unit.MediaFiles.Add(new VerifiedMediaFileFacts(Path.Combine(_directory, "BCS.S01E03.mkv"), 1024, ".mkv", "1080p", "H264", "WEB", Now));
            unit.TvEpisodes.LinkMediaFile(s2Ep1.Id, f2_1.Id, Now);
            // Notice: s2Ep2 has no file (or missing file), s2Ep3 has present file
            unit.TvEpisodes.LinkMediaFile(s2Ep3.Id, f2_3.Id, Now);

            // Show 3: Wholly unwatched -> must NOT appear in Continue Watching
            var s3 = unit.TvShows.Create(new TvShowCatalogData("tmdb", "s3", "The Wire", "the wire", 2002, "Overview", [], "poster3.jpg", MetadataStatus.Ready, backdropReference: "backdrop3.jpg"), Now.AddMinutes(-2));
            show3Id = s3.Id;
            var s3Season = unit.TvSeasons.Create(show3Id, 1, "Season 1", null, Now);
            var s3Ep1 = unit.TvEpisodes.Create(s3Season.Id, 1, "The Target", null, null, null, Now);
            var f3_1 = unit.MediaFiles.Add(new VerifiedMediaFileFacts(Path.Combine(_directory, "Wire.S01E01.mkv"), 1024, ".mkv", "1080p", "H264", "WEB", Now));
            unit.TvEpisodes.LinkMediaFile(s3Ep1.Id, f3_1.Id, Now);

            // Show 4: Fully watched -> must NOT appear in Continue Watching
            var s4 = unit.TvShows.Create(new TvShowCatalogData("tmdb", "s4", "Chernobyl", "chernobyl", 2019, "Overview", [], "poster4.jpg", MetadataStatus.Ready, backdropReference: "backdrop4.jpg"), Now.AddMinutes(-1));
            show4Id = s4.Id;
            var s4Season = unit.TvSeasons.Create(show4Id, 1, "Season 1", null, Now);
            var s4Ep1 = unit.TvEpisodes.Create(s4Season.Id, 1, "1:23:45", null, null, null, Now);
            var f4_1 = unit.MediaFiles.Add(new VerifiedMediaFileFacts(Path.Combine(_directory, "Chernobyl.S01E01.mkv"), 1024, ".mkv", "1080p", "H264", "WEB", Now));
            unit.TvEpisodes.LinkMediaFile(s4Ep1.Id, f4_1.Id, Now);

            unit.Commit();
        }

        var personal = new PersonalLibraryRepository(_connectionString);
        // Watch Show 1 Episode 1
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var epRepo = new TvEpisodeRepository(connection);
            var s1Eps = epRepo.ListByShow(show1Id);
            personal.AddEpisodeWatchEvent(s1Eps.First(e => e.Number == 1).Id, Now, Now);

            // Watch Show 2 Episode 1
            var s2Eps = epRepo.ListByShow(show2Id);
            personal.AddEpisodeWatchEvent(s2Eps.First(e => e.Number == 1).Id, Now, Now);

            // Watch Show 4 Episode 1 (fully watched)
            var s4Eps = epRepo.ListByShow(show4Id);
            personal.AddEpisodeWatchEvent(s4Eps.First(e => e.Number == 1).Id, Now, Now);
        }

        // Test 1: Fast-Path SQL via TvShowRepository implementing ITvContinueWatchingReader
        IReadOnlyList<TvContinueWatchingItem> sqlResults;
        var swSql = Stopwatch.StartNew();
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var tvShowRepo = new TvShowRepository(connection);
            var tvSeasonRepo = new TvSeasonRepository(connection);
            var tvEpRepo = new TvEpisodeRepository(connection);
            var service = new TvLibraryService(tvShowRepo, tvSeasonRepo, tvEpRepo, personal);

            sqlResults = service.ListContinueWatching(20);
        }
        swSql.Stop();

        // Test 2: In-Memory Fallback via NonSqlTvShowRepository (does not implement ITvContinueWatchingReader)
        IReadOnlyList<TvContinueWatchingItem> fallbackResults;
        var swFallback = Stopwatch.StartNew();
        using (var connection = new SqliteConnection(_connectionString))
        {
            connection.Open();
            var tvShowRepo = new NonSqlTvShowRepository(new TvShowRepository(connection));
            var tvSeasonRepo = new TvSeasonRepository(connection);
            var tvEpRepo = new TvEpisodeRepository(connection);
            var service = new TvLibraryService(tvShowRepo, tvSeasonRepo, tvEpRepo, personal);

            fallbackResults = service.ListContinueWatching(20);
        }
        swFallback.Stop();
        _output.WriteLine($"[P1 Measured Performance] FastPath SQL: {swSql.Elapsed.TotalMilliseconds:F2}ms, In-Memory Fallback: {swFallback.Elapsed.TotalMilliseconds:F2}ms");

        // Assert 1: Both tiers return exactly 2 items (Show 2 and Show 1, ordered by date_added DESC)
        Assert.Equal(2, sqlResults.Count);
        Assert.Equal(2, fallbackResults.Count);

        // Assert 2: Exact item equality across both tiers
        for (int i = 0; i < sqlResults.Count; i++)
        {
            var sqlItem = sqlResults[i];
            var fbItem = fallbackResults[i];

            Assert.Equal(sqlItem.ShowId, fbItem.ShowId);
            Assert.Equal(sqlItem.ShowTitle, fbItem.ShowTitle);
            Assert.Equal(sqlItem.CustomDisplayTitle, fbItem.CustomDisplayTitle);
            Assert.Equal(sqlItem.CanonicalTitle, fbItem.CanonicalTitle);
            Assert.Equal(sqlItem.EpisodeId, fbItem.EpisodeId);
            Assert.Equal(sqlItem.SeasonNumber, fbItem.SeasonNumber);
            Assert.Equal(sqlItem.EpisodeNumber, fbItem.EpisodeNumber);
            Assert.Equal(sqlItem.EpisodeTitle, fbItem.EpisodeTitle);
            Assert.Equal(sqlItem.ArtworkReference, fbItem.ArtworkReference);
            Assert.Equal(sqlItem.IsAvailable, fbItem.IsAvailable);
        }

        // Assert 3: Show 2 is first because it was added more recently than Show 1
        var firstItem = sqlResults[0];
        Assert.Equal(show2Id, firstItem.ShowId);
        Assert.Equal("Better Call Saul", firstItem.CanonicalTitle);
        Assert.Equal(3, firstItem.EpisodeNumber); // Skipped missing ep 2, next present is ep 3!

        // Assert 4: Show 1 is second and carries custom display title
        var secondItem = sqlResults[1];
        Assert.Equal(show1Id, secondItem.ShowId);
        Assert.Equal("BrBa Custom", secondItem.ShowTitle);
        Assert.Equal("BrBa Custom", secondItem.CustomDisplayTitle);
        Assert.Equal("Breaking Bad", secondItem.CanonicalTitle);
        Assert.Equal(2, secondItem.EpisodeNumber); // Ep 2 is next
    }

    [Fact]
    public void NullHistory_ReturnsEmptyListImmediately()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var service = new TvLibraryService(
            new TvShowRepository(connection),
            new TvSeasonRepository(connection),
            new TvEpisodeRepository(connection),
            history: null);

        var result = service.ListContinueWatching(20);
        Assert.Empty(result);
    }

    [Fact]
    public void NegativeOrZeroLimit_ThrowsArgumentOutOfRangeException()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        var service = new TvLibraryService(
            new TvShowRepository(connection),
            new TvSeasonRepository(connection),
            new TvEpisodeRepository(connection),
            history: null);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.ListContinueWatching(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.ListContinueWatching(-1));
    }

    /// <summary>
    /// Wrapper that specifically hides ITvContinueWatchingReader to test the fallback tier.
    /// </summary>
    private sealed class NonSqlTvShowRepository(ITvShowRepository inner) : ITvShowRepository
    {
        public TvShow? GetById(int id) => inner.GetById(id);
        public TvShow? GetBySortTitle(string sortTitle) => inner.GetBySortTitle(sortTitle);
        public TvShow? GetByAlias(string aliasSortTitle) => inner.GetByAlias(aliasSortTitle);
        public void RegisterAlias(int showId, string aliasSortTitle, DateTimeOffset now) => inner.RegisterAlias(showId, aliasSortTitle, now);
        public void RecordOpened(int id, DateTimeOffset openedAt) => inner.RecordOpened(id, openedAt);
        public TvShow? GetByExternalId(string provider, string externalId) => inner.GetByExternalId(provider, externalId);
        public IReadOnlyList<TvShow> ListAll() => inner.ListAll();
        public int CountAll() => inner.CountAll();
        public TvShow Create(TvShowCatalogData data, DateTimeOffset now) => inner.Create(data, now);
        public TvShow UpdateMetadata(int id, TvShowCatalogData data, DateTimeOffset now) => inner.UpdateMetadata(id, data, now);
        public void SetCustomDisplayTitle(int id, string? customDisplayTitle) => inner.SetCustomDisplayTitle(id, customDisplayTitle);
        public void Delete(int id) => inner.Delete(id);
    }
}
