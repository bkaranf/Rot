namespace Rot.App.Updates;

public static class UpdatePaths
{
    public const string Repository = "bkaranf/Rot";
    public const string PackageAssetName = "Rot-win-x64.zip";
    public const string ChecksumsAssetName = "SHA256SUMS";
    public const long MaximumReleaseMetadataBytes = 2 * 1024 * 1024;
    public const long MaximumChecksumsBytes = 128 * 1024;
    public const long MaximumPackageBytes = 512L * 1024 * 1024;
    public const long MaximumExpandedPackageBytes = 512L * 1024 * 1024;
    public const int MaximumPackageEntries = 20_000;
    public const string RequiredExecutableName = "Rot.exe";
    public const string RequiredAssemblyName = "Rot.dll";
    public const string RequiredPlayerIndex = "Web/player/index.html";
    public const string UpdaterExecutableName = "Rot.Updater.exe";
    public static readonly Uri LatestReleaseUri = new("https://api.github.com/repos/bkaranf/Rot/releases/latest");

    public static string DefaultStagingRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new UpdateException("Rot could not determine the local application data folder.");
        }

        return Path.Combine(localAppData, "Rot", "Updates");
    }

    public static string CreateStagingDirectory(string stagingRoot)
    {
        var root = ValidateStagingRoot(stagingRoot);
        Directory.CreateDirectory(root);
        RejectReparsePointsAlongPath(root);
        var path = Path.Combine(root, $"update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RejectReparsePoint(path, allowMissing: false);
        File.WriteAllText(Path.Combine(path, ".rot-update-staging"), "Rot update staging folder\n");
        return path;
    }

    public static string ValidateStagingRoot(string stagingRoot)
    {
        if (string.IsNullOrWhiteSpace(stagingRoot))
        {
            throw new UpdateException("The update staging folder is required.");
        }

        var fullPath = Path.GetFullPath(stagingRoot);
        RejectReparsePointsAlongPath(fullPath);
        RejectReparsePoint(fullPath, allowMissing: true);
        var root = Path.GetPathRoot(fullPath);
        if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("The update staging folder cannot be a drive root.");
        }

        return fullPath;
    }

    public static string ValidateInstallDirectory(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            throw new UpdateException("The Rot installation folder is required.");
        }

        var fullPath = Path.GetFullPath(installDirectory).TrimEnd(Path.DirectorySeparatorChar);
        RejectReparsePointsAlongPath(fullPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(fullPath, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException("Rot cannot update a drive root.");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localRotData = string.IsNullOrWhiteSpace(localAppData)
            ? string.Empty
            : Path.Combine(localAppData, "Rot");
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (IsSamePath(fullPath, userProfile) || IsSamePath(fullPath, localAppData) ||
            IsSamePath(fullPath, localRotData) || IsSamePath(fullPath, documents) ||
            IsSamePath(fullPath, desktop))
        {
            throw new UpdateException("Rot cannot update a profile, preferences, documents, or desktop root.");
        }

        RejectReparsePoint(fullPath, allowMissing: false);
        if (Directory.Exists(Path.Combine(fullPath, ".git")) || Directory.Exists(Path.Combine(fullPath, ".hg")))
        {
            throw new UpdateException("Rot cannot update a repository root.");
        }

        var parent = Directory.GetParent(fullPath)?.FullName;
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new UpdateException("Rot installation has no safe parent folder.");
        }

        RejectReparsePoint(parent, allowMissing: false);
        return fullPath;
    }

    public static void RejectReparsePointsAlongPath(string path)
    {
        var current = Path.GetFullPath(path);
        var chain = new Stack<string>();
        while (!string.IsNullOrWhiteSpace(current))
        {
            chain.Push(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent ?? string.Empty;
        }

        while (chain.Count > 0)
        {
            var item = chain.Pop();
            if (!Directory.Exists(item))
            {
                continue;
            }

            var attributes = File.GetAttributes(item);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UpdateException($"Reparse points are not allowed in update paths: {item}");
            }
        }
    }

    public static bool IsOwnedStagingDirectory(string stagingDirectory)
    {
        var fullPath = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var name = Path.GetFileName(fullPath);
        if (name.Length != "update-".Length + 32 ||
            !name.StartsWith("update-", StringComparison.OrdinalIgnoreCase) ||
            !name[7..].All(Uri.IsHexDigit))
        {
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            return false;
        }

        try
        {
            RejectReparsePointsAlongPath(fullPath);
            var marker = Path.Combine(fullPath, ".rot-update-staging");
            return File.Exists(marker) &&
                !File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    public static void DeleteOwnedStagingDirectory(string stagingDirectory)
    {
        if (!IsOwnedStagingDirectory(stagingDirectory))
        {
            return;
        }

        var fullPath = Path.GetFullPath(stagingDirectory);
        RejectReparseEntriesBelow(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    public static void RejectReparseEntriesBelow(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        RejectReparsePoint(fullRoot, allowMissing: false);
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UpdateException($"Reparse points are not allowed in update paths: {entry}");
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                }
            }
        }
    }

    public static void RejectReparsePoint(string path, bool allowMissing)
    {
        if (!Directory.Exists(path))
        {
            if (allowMissing)
            {
                return;
            }

            throw new UpdateException($"The required folder does not exist: {path}");
        }

        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UpdateException($"Reparse points are not allowed in update paths: {path}");
        }
    }

    public static bool IsSamePath(string left, string right) =>
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    public static bool IsSameOrDescendant(string path, string possibleParent)
    {
        var child = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var parent = Path.GetFullPath(possibleParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return child.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || IsSamePath(path, possibleParent);
    }
}
