using System;
using System.Collections.Generic;
using System.IO;
using TBDMovies.Application.Repositories;
using TBDMovies.Application.Services;
using TBDMovies.Domain.Media.Discovery;
using TBDMovies.FileSystem.Discovery;
using TBDMovies.FileSystem.Inspection;
using Xunit;

namespace TBDMovies.Tests.Application;

public sealed class InboundAutoScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tbdmovies-inbound-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _inboundDir;

    public InboundAutoScanTests()
    {
        _inboundDir = Path.Combine(_root, "Inbound");
        Directory.CreateDirectory(_inboundDir);
    }

    private sealed class MemorySettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.TryGetValue(key, out var val) ? val : null;
        public void Set(string key, string value, DateTimeOffset updatedAt) => _values[key] = value;
        public void Delete(string key) => _values.Remove(key);
    }

    [Fact]
    public void ApprovedRootsReader_includes_configured_inbound_folder_and_has_no_downloads_fallback()
    {
        var settings = new MemorySettingsRepository();

        // Without ui_inbound_folder, no default Downloads folder is injected.
        var emptyRoots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: false, verifyDirectoryExists: true);
        Assert.Empty(emptyRoots);

        // When user explicitly configures ui_inbound_folder, it becomes an approved root.
        settings.Set("ui_inbound_folder", _inboundDir, DateTimeOffset.UtcNow);
        var configuredRoots = ApprovedRootsReader.ResolveApprovedRoots(settings, dataFolder: null, skipNetworkPaths: false, verifyDirectoryExists: true);
        Assert.Contains(_inboundDir, configuredRoots, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImportFileValidator_rejects_active_sidecar_and_locked_files()
    {
        var mediaPath = Path.Combine(_inboundDir, "TestMovie.2024.mkv");
        File.WriteAllBytes(mediaPath, new byte[128]);
        var discovered = Assert.Single(new MediaDiscoveryService().Discover(_inboundDir, false));

        // Adding an .aria2, .part, or .!qB sidecar must block ImportFileValidator.Validate.
        foreach (var suffix in new[] { ".aria2", ".part", ".!qB" })
        {
            var sidecarPath = mediaPath + suffix;
            File.WriteAllBytes(sidecarPath, new byte[8]);
            Assert.Throws<IOException>(() => new ImportFileValidator().Validate(discovered));
            File.Delete(sidecarPath);
        }

        // A file with a temporary download extension must also be rejected by ImportFileValidator.
        var tempExtPath = Path.Combine(_inboundDir, "TempMovie.2024.mkv.part");
        File.WriteAllBytes(tempExtPath, new byte[128]);
        var tempDiscovered = new DiscoveredMedia(
            tempExtPath,
            128,
            discovered.ParsedMedia,
            DiscoveryClassification.MovieCandidate,
            null,
            new FileObservation(128, File.GetLastWriteTimeUtc(tempExtPath)));
        Assert.Throws<IOException>(() => new ImportFileValidator().Validate(tempDiscovered));

        // Holding an exclusive write lock must also block ImportFileValidator.Validate.
        using (var writerLock = new FileStream(mediaPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => new ImportFileValidator().Validate(discovered));
        }

        // Once unlocked and sidecar-free, validation succeeds.
        new ImportFileValidator().Validate(discovered);
    }

    public void Dispose()
    {
        var path = Path.GetFullPath(_root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
