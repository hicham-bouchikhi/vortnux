using System.Security.Cryptography;

namespace Vortnux.Core;

public static class SafeFiles
{
    public static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Normalize(string path)
    {
        path = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') ||
            path.Split('/').Any(part => part is "." or ".." or "" || part.Any(char.IsControl)))
            throw new ModManagerException("Unsafe or ambiguous archive path: " + path);
        return path;
    }

    public static string Resolve(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, Normalize(relative)));
        var current = Path.GetFullPath(root);
        for (var ancestor = new DirectoryInfo(current); ancestor is not null; ancestor = ancestor.Parent)
            RejectLink(ancestor.FullName);
        foreach (var part in relative.Replace('\\', '/').Split('/'))
        {
            // Windows/Proton sees case aliases as the same file. Never create an alias on Linux.
            if (Directory.Exists(current))
            {
                var aliases = Directory.EnumerateFileSystemEntries(current)
                    .Select(Path.GetFileName).Where(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (aliases.Length > 1 || aliases.Length == 1 && aliases[0] != part)
                    throw new ModManagerException("Case collision in game directory: " + relative);
            }
            current = Path.Combine(current, part);
            RejectLink(current);
        }
        return path;
    }

    public static void RejectLink(string path)
    {
        // Also catches dangling symlinks, for which File.Exists is false.
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new ModManagerException("Symbolic links are not permitted in managed paths: " + path);
    }

    public static void AtomicCopy(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".vortnux-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var input = File.OpenRead(source))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
