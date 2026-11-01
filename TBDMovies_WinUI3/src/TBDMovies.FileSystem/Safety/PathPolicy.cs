using TBDMovies.Domain.Core.Operations;
using TBDMovies.Domain.Core.Safety;

namespace TBDMovies.FileSystem.Safety;

public sealed record ValidatedPathPlan(string Source, string Destination, SourceIdentity Identity);

public sealed class PathPolicy
{
    private readonly string[] _approvedRoots;

    public PathPolicy(IEnumerable<string> approvedRoots)
    {
        ArgumentNullException.ThrowIfNull(approvedRoots);
        _approvedRoots = approvedRoots.Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_approvedRoots.Length == 0) throw new ArgumentException("At least one approved root is required.");
        foreach (var root in _approvedRoots)
        {
            AssertNoReparseComponents(root);
            if (!Directory.Exists(root)) throw new UnsafePathException($"Approved root is unavailable: {root}");
        }
    }

    public IReadOnlyList<string> ApprovedRoots => _approvedRoots;

    public ValidatedPathPlan ValidatePlan(string source, string destination, OperationType operationType)
    {
        if (!Enum.IsDefined(operationType)) throw new ArgumentOutOfRangeException(nameof(operationType));
        var sourcePath = Canonical(source);
        var destinationPath = Canonical(destination);
        AssertNoReparseComponents(sourcePath);
        AssertNoReparseComponents(Path.GetDirectoryName(destinationPath)!);
        AssertApproved(sourcePath);
        AssertApproved(destinationPath);

        if (!File.Exists(sourcePath)) throw new SourceMissingException($"Source is unavailable: {sourcePath}");
        var destinationParent = Path.GetDirectoryName(destinationPath)!;
        if (!Directory.Exists(destinationParent))
            throw new UnsafePathException($"Destination parent is unavailable: {destinationParent}");

        if (operationType == OperationType.Rename &&
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(sourcePath), destinationParent))
            throw new InvalidRenameException("Rename operations must stay in the same directory.");

        if (StringComparer.OrdinalIgnoreCase.Equals(sourcePath, destinationPath))
            throw new SameFileException("Source and destination are the same Windows path.");

        var desiredName = Path.GetFileName(destinationPath);
        foreach (var entry in Directory.EnumerateFileSystemEntries(destinationParent))
        {
            var existingName = Path.GetFileName(entry);
            if (!StringComparer.OrdinalIgnoreCase.Equals(existingName, desiredName)) continue;
            if (!StringComparer.Ordinal.Equals(existingName, desiredName))
                throw new CaseInsensitiveCollisionException($"Case-insensitive destination collision: {existingName}");
            if (File.Exists(entry) && WindowsNative.Identity(entry) == WindowsNative.Identity(sourcePath))
                throw new SameFileException("Source and destination identify the same file.");
            throw new DestinationExistsException($"Destination already exists: {destinationPath}");
        }

        return new ValidatedPathPlan(sourcePath, destinationPath, WindowsNative.Identity(sourcePath));
    }

    public SourceIdentity Revalidate(FileOperationRecord record)
    {
        var validated = ValidatePlan(record.Source, record.Destination, record.OperationType);
        var expected = new SourceIdentity(
            record.SourceSize ?? throw new SourceChangedException("Journal lacks source size."),
            record.SourceMTimeNs ?? throw new SourceChangedException("Journal lacks source timestamp."),
            record.SourceDev ?? throw new SourceChangedException("Journal lacks source volume identity."),
            record.SourceIno ?? throw new SourceChangedException("Journal lacks source file identity."));
        if (validated.Identity != expected) throw new SourceChangedException("Source identity changed after planning.");
        return validated.Identity;
    }

    public string ValidateExistingRecoveryPath(string path)
    {
        var canonical = Canonical(path);
        AssertNoReparseComponents(canonical);
        AssertApproved(canonical);
        if (!File.Exists(canonical)) throw new UnsafePathException($"Recovery path is unavailable: {canonical}");
        return canonical;
    }

    public static SourceIdentity Identity(string path) => WindowsNative.Identity(Canonical(path));

    internal static void AssertNoReparseComponents(string path)
    {
        var full = Canonical(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new LinkTraversalException($"Link/reparse traversal is not allowed: {current}");
        }
    }

    private void AssertApproved(string candidate)
    {
        foreach (var root in _approvedRoots)
        {
            var relative = Path.GetRelativePath(root, candidate);
            if (relative == "." ||
                (!relative.Equals("..", StringComparison.Ordinal) &&
                 !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                 !Path.IsPathRooted(relative)))
                return;
        }
        throw new UnsafePathException($"Path is outside approved roots: {candidate}");
    }

    private static readonly HashSet<string> ReservedDosDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

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

    private static bool IsDosDeviceName(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return false;

        var trimmed = segment.TrimEnd(' ', '.');
        if (ReservedDosDeviceNames.Contains(trimmed))
            return true;

        var dotIndex = trimmed.IndexOf('.');
        if (dotIndex > 0)
        {
            var baseName = trimmed[..dotIndex];
            if (ReservedDosDeviceNames.Contains(baseName))
                return true;
        }

        return false;
    }

    private static void AssertNoDosDeviceNames(string path)
    {
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (IsDosDeviceName(segment))
            {
                throw new UnsafePathException($"DOS reserved device name is not allowed: {segment}");
            }
        }
    }

    private static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new UnsafePathException("Path must be absolute.");

        if (HasAlternateDataStream(path))
            throw new UnsafePathException($"NTFS alternate data streams are not allowed: {path}");

        AssertNoDosDeviceNames(path);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is not UnsafePathException)
        {
            throw new UnsafePathException($"Invalid path: {path}");
        }

        if (HasAlternateDataStream(fullPath))
            throw new UnsafePathException($"NTFS alternate data streams are not allowed: {fullPath}");

        AssertNoDosDeviceNames(fullPath);

        return fullPath;
    }
}
