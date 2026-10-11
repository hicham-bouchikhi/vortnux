using System.IO.Compression;

namespace Vortnux.Core;

public sealed class ArchiveStager(string dataDirectory)
{
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xedb88320u : value >> 1;
            table[index] = value;
        }
        return table;
    }
    public ManagedMod Stage(string archive, string gameId, string? name = null, string? collectionId = null)
    {
        using (var input = File.OpenRead(archive))
        {
            Span<byte> magic = stackalloc byte[4];
            if (input.Length < 4 || input.Read(magic) != 4 || magic[0] != 0x50 || magic[1] != 0x4b)
            {
                if (magic[0] != 0x37 || magic[1] != 0x7a)
                    throw new ModManagerException("This build supports ZIP and 7z archives. RAR and other formats require separate validation.");
                var converted = SevenZip.ConvertToZip(archive, Path.Combine(dataDirectory, "temporary-archives"));
                try { return Stage(converted, gameId, name ?? Path.GetFileNameWithoutExtension(archive), collectionId); }
                finally { File.Delete(converted); }
            }
        }
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 20000 || zip.Entries.Sum(entry => entry.Length) > 8L * 1024 * 1024 * 1024)
            throw new ModManagerException("Archive exceeds the extraction size or file-count limit.");
        var entries = zip.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')).ToArray();
        var paths = entries.Select(entry => SafeFiles.Normalize(entry.FullName)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new ModManagerException("Archive contains duplicate or case-colliding files.");
        if (paths.Any(path => path.Split('/').Any(part => part.Equals("fomod", StringComparison.OrdinalIgnoreCase))))
            throw new ModManagerException("This archive requires FOMOD installer choices, which this build cannot reproduce yet.");
        var mapped = paths.Select(MapCyberpunkPath).ToArray();
        if (mapped.Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase).Count() != mapped.Count(path => path is not null))
            throw new ModManagerException("Multiple archive files map to the same game file.");
        var filePaths = mapped.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in filePaths)
        {
            var parts = target.Split('/');
            for (var count = 1; count <= parts.Length; count++)
            {
                var prefix = string.Join('/', parts.Take(count));
                if (prefixes.TryGetValue(prefix, out var existing) && existing != prefix)
                    throw new ModManagerException("Case collision between archive directories: " + target);
                prefixes[prefix] = prefix;
                if (count < parts.Length && filePaths.Contains(prefix))
                    throw new ModManagerException("Archive has a file/directory collision: " + prefix);
            }
        }
        var blobDirectory = Path.Combine(dataDirectory, "blobs");
        Directory.CreateDirectory(blobDirectory);
        var files = new List<ModFile>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType is not 0 and not 0x8000) throw new ModManagerException("Archive contains a link or special file.");
            if (mapped[index] is not { } target) continue;
            var temporary = Path.Combine(blobDirectory, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (var input = entry.Open())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    // Bound actual bytes as well as claimed sizes.
                    var buffer = new byte[65536];
                    long copied = 0;
                    uint crc = uint.MaxValue;
                    int read;
                    while ((read = input.Read(buffer)) != 0)
                    {
                        copied += read;
                        if (copied > entry.Length || copied > 2L * 1024 * 1024 * 1024)
                            throw new ModManagerException("Archive entry exceeds the extraction limit.");
                        output.Write(buffer, 0, read);
                        for (var offset = 0; offset < read; offset++) crc = CrcTable[(byte)(crc ^ buffer[offset])] ^ (crc >> 8);
                    }
                    if (copied != entry.Length) throw new ModManagerException("Archive entry is truncated.");
                    if (~crc != entry.Crc32) throw new ModManagerException("Archive checksum failed: " + paths[index]);
                    output.Flush(true);
                }
                var hash = SafeFiles.Hash(temporary);
                var blob = Path.Combine(blobDirectory, hash);
                if (!File.Exists(blob)) File.Move(temporary, blob);
                else if (SafeFiles.Hash(blob) != hash) throw new ModManagerException("Staging cache is corrupted.");
                files.Add(new ModFile(target, hash, blob));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (files.Count == 0) throw new ModManagerException("No supported Cyberpunk files were found in this archive.");
        return new ManagedMod(Guid.NewGuid().ToString("N"), gameId, name ?? Path.GetFileNameWithoutExtension(archive),
            true, files.OrderBy(file => file.Path, StringComparer.Ordinal).ToList(), collectionId);
    }

    private static string? MapCyberpunkPath(string path)
    {
        string[] roots = ["archive/pc/mod/", "r6/scripts/", "r6/tweaks/", "red4ext/", "bin/x64/"];
        foreach (var root in roots)
        {
            if (path.StartsWith(root, StringComparison.Ordinal)) return path;
            var position = path.IndexOf('/' + root, StringComparison.Ordinal);
            if (position >= 0) return path[(position + 1)..];
        }
        if (!path.Contains('/') && path.EndsWith(".archive", StringComparison.OrdinalIgnoreCase)) return "archive/pc/mod/" + path;
        if (Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".md" or ".png" or ".jpg" or ".pdf") return null;
        throw new ModManagerException("Unsupported installer layout: " + path + ". No game files were changed.");
    }
}
