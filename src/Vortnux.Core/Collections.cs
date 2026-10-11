using System.Security.Cryptography;
using System.Text.Json;

namespace Vortnux.Core;

public sealed record CollectionEntry(string Name, long ModId, long FileId, bool Optional, string? Md5, long? Size);
public sealed record CollectionPlan(string Name, List<CollectionEntry> Entries);

public static class Collections
{
    // A deliberately bounded adapter for declarative Cyberpunk collections. Never silently skip installer rules.
    public static CollectionPlan Parse(JsonElement document)
    {
        var allowed = new HashSet<string>(["info", "mods", "modRules"], StringComparer.Ordinal);
        foreach (var property in document.EnumerateObject())
            if (!allowed.Contains(property.Name) && Meaningful(property.Value))
                throw new ModManagerException("Unsupported collection feature: " + property.Name);
        var info = document.GetProperty("info");
        if (info.GetProperty("domainName").GetString() != "cyberpunk2077")
            throw new ModManagerException("Collection targets another game.");
        Reject(info, "installInstructions", "gameVersions");
        Reject(document, "modRules");
        var entries = new List<CollectionEntry>();
        foreach (var mod in document.GetProperty("mods").EnumerateArray())
        {
            if (mod.GetProperty("domainName").GetString() != "cyberpunk2077")
                throw new ModManagerException("Collection contains another game's mod.");
            Reject(mod, "choices", "patches", "instructions", "fileOverrides", "phase");
            if (mod.TryGetProperty("details", out var details)) Reject(details, "type");
            var source = mod.GetProperty("source");
            if (source.GetProperty("type").GetString() != "nexus")
                throw new ModManagerException("This collection requires external or bundled resources.");
            Reject(source, "instructions", "fileExpression");
            if (source.TryGetProperty("updatePolicy", out var policy) && policy.GetString() is not (null or "exact"))
                throw new ModManagerException("Collection requires dynamic file selection. Pin an exact revision first.");
            var modId = source.GetProperty("modId").GetInt64();
            var fileId = source.GetProperty("fileId").GetInt64();
            if (modId <= 0 || fileId <= 0) throw new ModManagerException("Collection must pin valid file IDs.");
            var hash = source.TryGetProperty("md5", out var md5) ? md5.GetString() : null;
            if (hash is not null && (hash.Length != 32 || hash.Any(c => !Uri.IsHexDigit(c))))
                throw new ModManagerException("Invalid collection checksum.");
            entries.Add(new CollectionEntry(mod.GetProperty("name").GetString()!, modId, fileId,
                mod.GetProperty("optional").GetBoolean(), hash,
                source.TryGetProperty("fileSize", out var size) ? size.GetInt64() : null));
        }
        if (entries.Count is 0 or > 2000 || entries.DistinctBy(entry => entry.FileId).Count() != entries.Count)
            throw new ModManagerException("Collection has no files, too many files, or duplicate file IDs.");
        return new CollectionPlan(info.GetProperty("name").GetString()!, entries);
    }

    public static void VerifyDownload(string path, CollectionEntry entry)
    {
        if (entry.Size.HasValue && new FileInfo(path).Length != entry.Size.Value)
            throw new ModManagerException("Collection download size mismatch: " + entry.Name);
        if (entry.Md5 is not null)
        {
            // Nexus manifests use MD5 for identity; our deployment cache uses SHA-256.
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(MD5.HashData(stream));
            if (!string.Equals(actual, entry.Md5, StringComparison.OrdinalIgnoreCase))
                throw new ModManagerException("Collection download checksum mismatch: " + entry.Name);
        }
    }

    private static bool Meaningful(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.False => false,
        JsonValueKind.Number => value.GetDouble() != 0,
        _ => true
    };

    private static void Reject(JsonElement value, params string[] properties)
    {
        foreach (var name in properties)
            if (value.TryGetProperty(name, out var item) && Meaningful(item))
                throw new ModManagerException("Collection needs unsupported installer behavior: " + name + ". No game files were changed.");
    }
}
