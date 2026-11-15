namespace VideoFactory.VideoProduction.Infrastructure.Storage;

internal static class SafeFileTree
{
    internal static long SizeBytes(string directory, CancellationToken token)
    {
        if (!Directory.Exists(directory)) return 0;
        RejectLink(directory);
        long total = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            RejectLink(entry);
            total = checked(total + (Directory.Exists(entry)
                ? SizeBytes(entry, token) : new FileInfo(entry).Length));
        }
        return total;
    }

    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Linked paths are not allowed in artifact storage.");
    }

    internal static string Resolve(string root, string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (!IsSafeRelativePath(relativePath))
            throw new IOException("Artifact path must be a safe relative path.");
        string fullRoot = Path.GetFullPath(root);
        string current = fullRoot;
        if (Directory.Exists(current)) RejectLink(current);
        foreach (string part in normalized.Split('/'))
        {
            current = Path.Combine(current, part);
            if (Path.Exists(current)) RejectLink(current);
        }
        return current;
    }

    internal static bool IsSafeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(normalized) && !normalized.StartsWith('/')
            && !normalized.Any(character => char.IsControl(character) || ":<>\"|?*".Contains(character))
            && !normalized.Split('/').Any(part => part is ".." or "." or "" || part.EndsWith('.') || part.EndsWith(' '));
    }
}
