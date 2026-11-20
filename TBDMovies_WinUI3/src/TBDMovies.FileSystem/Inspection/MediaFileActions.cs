using System.Diagnostics;
using TBDMovies.Application.External;
using TBDMovies.Domain.Library.Availability;
using TBDMovies.Domain.Media;

namespace TBDMovies.FileSystem.Inspection;

public sealed record FileActionResult(AvailabilityInspectionStatus Status, string? ErrorCode = null);

/// <summary>Read-only shell actions. Availability checks and shell activation run away from the UI.</summary>
public sealed class MediaFileActions
{
    private readonly IAvailabilityInspector _inspector;
    private readonly Action<string, bool> _launch;

    public MediaFileActions(IAvailabilityInspector inspector, Action<string, bool>? launch = null)
    {
        _inspector = inspector;
        _launch = launch ?? Launch;
    }

    public Task<FileActionResult> OpenFileAsync(string path, CancellationToken token = default) =>
        OpenAsync(path, selectFile: false, directory: false, token);

    public Task<FileActionResult> OpenContainingFolderAsync(string path, CancellationToken token = default) =>
        OpenAsync(path, selectFile: true, directory: false, token);

    public Task<FileActionResult> OpenFolderAsync(string path, CancellationToken token = default) =>
        OpenAsync(path, selectFile: false, directory: true, token);

    private Task<FileActionResult> OpenAsync(string path, bool selectFile, bool directory, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || HasAlternateDataStream(path))
                    return new FileActionResult(AvailabilityInspectionStatus.Error, "InvalidPath");

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(path).TrimEnd(' ', '.');
                }
                catch (Exception)
                {
                    return new FileActionResult(AvailabilityInspectionStatus.Error, "InvalidPath");
                }

                if (HasAlternateDataStream(fullPath))
                    return new FileActionResult(AvailabilityInspectionStatus.Error, "InvalidPath");

                if (!selectFile && !directory && !MediaFormatPolicy.IsSupportedVideoPath(fullPath))
                    return new FileActionResult(AvailabilityInspectionStatus.Error, "UnsupportedMediaType");

                var status = directory ? InspectFolder(fullPath, token) : _inspector.Inspect(fullPath).Status;
                token.ThrowIfCancellationRequested();
                if (status != AvailabilityInspectionStatus.Present) return new FileActionResult(status);

                _launch(fullPath, selectFile);
                return new FileActionResult(AvailabilityInspectionStatus.Present);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
            {
                token.ThrowIfCancellationRequested();
                // The share can disconnect between verification and shell activation.
                return new FileActionResult(_inspector.IsStorageAvailable(path)
                    ? AvailabilityInspectionStatus.Error : AvailabilityInspectionStatus.StorageUnavailable,
                    error.GetType().Name);
            }
        }, token);

    private AvailabilityInspectionStatus InspectFolder(string path, CancellationToken token) => NetworkReadGuard.Shared.Execute(
        path,
        () =>
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.Directory) != 0
                    ? AvailabilityInspectionStatus.Present : AvailabilityInspectionStatus.Missing;
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // Use a raw share-root probe here: nesting the shared guard would report its own work busy.
                var root = Path.GetPathRoot(Path.GetFullPath(path))!;
                if (NetworkReadGuard.IsNetworkPath(path))
                {
                    try { return (File.GetAttributes(root) & FileAttributes.Directory) != 0
                        ? AvailabilityInspectionStatus.Missing : AvailabilityInspectionStatus.StorageUnavailable; }
                    catch (Exception rootError) when (rootError is IOException or UnauthorizedAccessException)
                    { return AvailabilityInspectionStatus.StorageUnavailable; }
                }
                return _inspector.IsStorageAvailable(path)
                    ? AvailabilityInspectionStatus.Missing : AvailabilityInspectionStatus.StorageUnavailable;
            }
        },
        () => AvailabilityInspectionStatus.StorageUnavailable,
        () => token.IsCancellationRequested);

    private static bool HasAlternateDataStream(string path)
    {
        int allowedDriveColonIndex = -1;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) && path.Length >= 6 && char.IsLetter(path[4]) && path[5] == ':')
        {
            allowedDriveColonIndex = 5;
        }
        else if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            allowedDriveColonIndex = 1;
        }

        int colonIndex = path.IndexOf(':');
        while (colonIndex != -1)
        {
            if (colonIndex != allowedDriveColonIndex)
            {
                return true;
            }
            colonIndex = path.IndexOf(':', colonIndex + 1);
        }

        return false;
    }

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly string SystemExplorerPath =
        string.IsNullOrWhiteSpace(WindowsDirectory) ? "explorer.exe" : Path.Combine(WindowsDirectory, "explorer.exe");

    private static void Launch(string path, bool selectFile)
    {
        var cleanPath = Path.GetFullPath(path).TrimEnd(' ', '.').Replace("\"", "");
        ProcessStartInfo start;
        if (selectFile)
        {
            start = new ProcessStartInfo(SystemExplorerPath)
            {
                Arguments = $"/select,\"{cleanPath}\"",
                UseShellExecute = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(WindowsDirectory) ? string.Empty : WindowsDirectory
            };
        }
        else if (Directory.Exists(cleanPath))
        {
            start = new ProcessStartInfo(SystemExplorerPath)
            {
                Arguments = $"\"{cleanPath}\"",
                UseShellExecute = true,
                WorkingDirectory = string.IsNullOrWhiteSpace(WindowsDirectory) ? string.Empty : WindowsDirectory
            };
        }
        else
        {
            var workingDir = Path.GetDirectoryName(cleanPath);
            start = new ProcessStartInfo(cleanPath)
            {
                UseShellExecute = true,
                WorkingDirectory = !string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir)
                    ? workingDir
                    : (string.IsNullOrWhiteSpace(WindowsDirectory) ? string.Empty : WindowsDirectory)
            };
        }

        using var process = Process.Start(start);
    }
}
