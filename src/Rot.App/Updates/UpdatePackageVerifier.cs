using System.IO.Compression;

namespace Rot.App.Updates;

public static class UpdatePackageVerifier
{
    private const string ExpectedRootName = "Rot-win-x64";

    public static string ValidateAndExtract(string archivePath, string extractionDirectory)
    {
        if (!File.Exists(archivePath))
        {
            throw new UpdateException("The downloaded update archive is missing.");
        }

        var extractionRoot = Path.GetFullPath(extractionDirectory);
        UpdatePaths.RejectReparsePointsAlongPath(extractionRoot);
        UpdatePaths.RejectReparsePoint(extractionRoot, allowMissing: true);
        Directory.CreateDirectory(extractionRoot);
        var payloadRoot = Path.Combine(extractionRoot, ExpectedRootName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0)
            {
                throw new UpdateException("The update archive is empty.");
            }

            if (archive.Entries.Count > UpdatePaths.MaximumPackageEntries)
            {
                throw new UpdateException("The update archive contains too many entries.");
            }

            long expandedBytes = 0;

            foreach (var entry in archive.Entries)
            {
                var relativePath = NormalizeEntryPath(entry.FullName, out var isDirectory);
                if (relativePath.Length == 0)
                {
                    continue;
                }

                if (!seen.Add(relativePath))
                {
                    throw new UpdateException($"The update archive contains a duplicate entry: {relativePath}");
                }

                if (!relativePath.Equals(ExpectedRootName, StringComparison.Ordinal) &&
                    !relativePath.StartsWith(ExpectedRootName + "/", StringComparison.Ordinal))
                {
                    throw new UpdateException("The update archive has an unexpected root folder.");
                }

                if (IsSymlink(entry))
                {
                    throw new UpdateException($"The update archive contains a link entry: {relativePath}");
                }

                var destination = Path.Combine(extractionRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                EnsureContained(destination, extractionRoot);
                if (isDirectory)
                {
                    UpdatePaths.RejectReparsePoint(destination, allowMissing: true);
                    Directory.CreateDirectory(destination);
                    continue;
                }

                if (entry.Length < 0 || entry.Length > UpdatePaths.MaximumExpandedPackageBytes ||
                    expandedBytes > UpdatePaths.MaximumExpandedPackageBytes - entry.Length)
                {
                    throw new UpdateException("The update archive expands beyond the allowed size.");
                }

                expandedBytes += entry.Length;

                var parent = Path.GetDirectoryName(destination);
                if (string.IsNullOrWhiteSpace(parent))
                {
                    throw new UpdateException("The update archive contains a file without a parent folder.");
                }

                Directory.CreateDirectory(parent);
                UpdatePaths.RejectReparsePoint(parent, allowMissing: false);
                if (!seenFiles.Add(relativePath))
                {
                    throw new UpdateException($"The update archive contains a duplicate file: {relativePath}");
                }

                entry.ExtractToFile(destination, overwrite: false);
            }

            ValidatePayloadRoot(payloadRoot);
            return payloadRoot;
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new UpdateException("The update archive could not be extracted safely.", exception);
        }
    }

    public static void ValidatePayloadRoot(string payloadRoot)
    {
        var root = Path.GetFullPath(payloadRoot);
        if (!Directory.Exists(root))
        {
            throw new UpdateException("The update payload root is missing.");
        }

        UpdatePaths.RejectReparsePoint(root, allowMissing: false);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.Count > 0)
        {
            var directory = pendingDirectories.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UpdateException($"The update payload contains a reparse point: {path}");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pendingDirectories.Push(path);
                }
            }
        }

        RequireFile(root, UpdatePaths.RequiredExecutableName);
        RequireFile(root, UpdatePaths.RequiredAssemblyName);
        RequireFile(root, UpdatePaths.RequiredPlayerIndex);
    }

    private static void RequireFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        EnsureContained(path, root);
        if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UpdateException($"The update payload is missing {relativePath}.");
        }
    }

    private static string NormalizeEntryPath(string value, out bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UpdateException("The update archive contains an empty entry.");
        }

        var normalized = value.Replace('\\', '/');
        isDirectory = normalized.EndsWith("/", StringComparison.Ordinal);
        normalized = normalized.TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalized) || (normalized.Length >= 2 && normalized[1] == ':'))
        {
            throw new UpdateException($"The update archive contains an absolute entry: {value}");
        }

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.Contains(':') ||
            segment.EndsWith(" ", StringComparison.Ordinal) ||
            segment.EndsWith(".", StringComparison.Ordinal) ||
            segment.Any(character => character < 32 || character == 127) ||
            IsReservedWindowsName(segment)))
        {
            throw new UpdateException($"The update archive contains an invalid Windows entry: {value}");
        }

        return string.Join('/', segments);
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        var unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixFileType == 0xA000;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var stem = segment.Split('.', 2)[0].TrimEnd(' ', '.');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                  stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return stem[3] is >= '1' and <= '9';
        }

        return false;
    }

    private static void EnsureContained(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !UpdatePaths.IsSamePath(fullPath, root))
        {
            throw new UpdateException("The update path escapes its staging folder.");
        }
    }
}
