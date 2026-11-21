using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class ApprovedRootsReaderTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _folderA;
    private readonly string _folderB;

    public ApprovedRootsReaderTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "tbdmovies_roots_test_" + Guid.NewGuid().ToString("N"));
        _folderA = Path.Combine(_testRoot, "FolderA");
        _folderB = Path.Combine(_testRoot, "FolderB");
        Directory.CreateDirectory(_folderA);
        Directory.CreateDirectory(_folderB);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
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
    public void Reads_Newline_Separated_Media_Folders()
    {
        var settings = new MemorySettingsRepository();
        settings.Set("ui_media_folders", $"{_folderA}\n{_folderB}", DateTimeOffset.UtcNow);

        var roots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: false, verifyDirectoryExists: true);

        Assert.Contains(_folderA, roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(_folderB, roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reads_Json_Array_Formatted_Media_Folders_Defensively()
    {
        var settings = new MemorySettingsRepository();
        // Emulating historical test fixture format: ["pathA", "pathB"]
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { _folderA, _folderB });
        settings.Set("ui_media_folders", json, DateTimeOffset.UtcNow);

        var roots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: false, verifyDirectoryExists: true);

        Assert.Contains(_folderA, roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(_folderB, roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Falls_Back_To_Legacy_Movie_And_Show_Folders()
    {
        var settings = new MemorySettingsRepository();
        settings.Set("ui_movie_folder", _folderA, DateTimeOffset.UtcNow);
        settings.Set("ui_show_folder", _folderB, DateTimeOffset.UtcNow);

        var roots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: false, verifyDirectoryExists: true);

        Assert.Contains(_folderA, roots, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(_folderB, roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_Relative_Paths()
    {
        var settings = new MemorySettingsRepository();
        settings.Set("ui_media_folders", "relative/path\n..\\another\\relative", DateTimeOffset.UtcNow);

        var roots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: false, verifyDirectoryExists: false);

        Assert.Empty(roots);
    }

    [Fact]
    public void Skips_Network_Paths_When_Requested()
    {
        var settings = new MemorySettingsRepository();
        var uncPath = @"\\nas\movies";
        var uncDevice = @"\\?\UNC\server\share\movies";
        settings.Set("ui_media_folders", $"{_folderA}\n{uncPath}\n{uncDevice}", DateTimeOffset.UtcNow);

        var roots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: true, verifyDirectoryExists: false);

        Assert.Contains(_folderA, roots, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(uncPath, roots, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(uncDevice, roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Includes_DataFolder_Even_When_Settings_Is_Null()
    {
        var roots = ApprovedRootsReader.ResolveApprovedRoots(settings: null, dataFolder: _folderA, skipNetworkPaths: false, verifyDirectoryExists: true);

        Assert.Single(roots);
        Assert.Equal(_folderA, roots[0], ignoreCase: true);
    }

    [Fact]
    public void Single_Malformed_Path_Does_Not_Abort_Reading_Remaining_Valid_Directories()
    {
        var settings = new MemorySettingsRepository();
        // Path with invalid characters in Windows
        var malformed = "C:\\invalid|folder*name";
        settings.Set("ui_media_folders", $"{malformed}\n{_folderA}", DateTimeOffset.UtcNow);

        var roots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: false, verifyDirectoryExists: false);

        Assert.Contains(_folderA, roots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invokes_OnError_When_Encountering_Malformed_Data()
    {
        var settings = new MemorySettingsRepository();
        settings.Set("ui_media_folders", "[malformed_json_not_array", DateTimeOffset.UtcNow);

        var errors = new List<Exception>();
        var roots = ApprovedRootsReader.ResolveApprovedRoots(
            settings,
            dataFolder: null,
            skipNetworkPaths: false,
            verifyDirectoryExists: false,
            onError: ex => errors.Add(ex));

        Assert.NotEmpty(errors);
    }
}
