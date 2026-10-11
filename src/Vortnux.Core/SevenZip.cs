using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Vortnux.Core;

public sealed record ArchiveEntry(string Path, long Size);

public static class SevenZip
{
    public static List<ArchiveEntry> Inventory(string archive)
    {
        using var output = new MemoryStream();
        Run(["l", "-slt", "-ba", "-sccUTF-8", "-pVortnuxEncryptedArchivesAreUnsupported", "--", Path.GetFullPath(archive)], output, 16 * 1024 * 1024);
        var entries = new List<ArchiveEntry>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        void Finish()
        {
            if (fields.Count == 0) return;
            var name = SafeFiles.Normalize(fields.GetValueOrDefault("Path") ?? throw new ModManagerException("Archive entry has no path."));
            var attributes = fields.GetValueOrDefault("Attributes", "");
            if (fields.GetValueOrDefault("Encrypted") == "+" || fields.ContainsKey("Symbolic Link") || fields.ContainsKey("Hard Link") ||
                attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => part.StartsWith('l')))
                throw new ModManagerException("Encrypted files and archive links are unsupported.");
            if (fields.GetValueOrDefault("Folder") != "+" && !attributes.StartsWith('D'))
            {
                if (!long.TryParse(fields.GetValueOrDefault("Size"), out var size) || size < 0)
                    throw new ModManagerException("Archive entry has an invalid size.");
                entries.Add(new ArchiveEntry(name, size));
            }
            fields.Clear();
        }
        using var reader = new StringReader(Encoding.UTF8.GetString(output.ToArray()));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) { Finish(); continue; }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator < 1 || !fields.TryAdd(line[..separator], line[(separator + 3)..]))
                throw new ModManagerException("Ambiguous archive metadata.");
        }
        Finish();
        if (entries.Count > 20000 || entries.Sum(entry => entry.Size) > 8L * 1024 * 1024 * 1024 ||
            entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new ModManagerException("Archive exceeds limits or contains duplicate paths.");
        return entries;
    }

    public static void Extract(string archive, ArchiveEntry entry, Stream destination, long limit)
    {
        if (entry.Size > limit) throw new ModManagerException("Archive entry exceeds the extraction limit.");
        var length = Run(["x", "-so", "-spd", "-ssc", "-bd", "-y", "-pVortnuxEncryptedArchivesAreUnsupported", "--", Path.GetFullPath(archive), entry.Path], destination, Math.Min(limit, entry.Size));
        if (length != entry.Size) throw new ModManagerException("Archive entry is truncated.");
    }

    public static string ConvertToZip(string archive, string directory)
    {
        var entries = Inventory(archive);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var entry in entries)
            {
                var target = zip.CreateEntry(entry.Path, CompressionLevel.NoCompression);
                using var output = target.Open(); Extract(archive, entry, output, 2L * 1024 * 1024 * 1024);
            }
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    private static long Run(string[] arguments, Stream output, long limit)
        => RunAsync(arguments, output, limit).GetAwaiter().GetResult();

    private static async Task<long> RunAsync(string[] arguments, Stream output, long limit)
    {
        var start = new ProcessStartInfo("7z") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["LC_ALL"] = "C.UTF-8";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new ModManagerException("Cannot start 7-Zip."); }
        catch (System.ComponentModel.Win32Exception) { throw new ModManagerException("Install the 7zip package to read 7z archives and Nexus collection bundles."); }
        using (process)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
        {
            process.StandardInput.Close();
            var errors = DrainErrors(process.StandardError, timeout.Token);
            try
            {
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    total += read;
                    if (total > limit) throw new ModManagerException("Archive extraction exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                await process.WaitForExitAsync(timeout.Token); await errors;
                if (process.ExitCode != 0) throw new ModManagerException("7-Zip could not read this archive. It may be damaged, encrypted or unsupported.");
                return total;
            }
            catch
            {
                if (!process.HasExited) process.Kill(true);
                timeout.Cancel();
                try { await errors; } catch (OperationCanceledException) { /* Drain ended after process termination. */ }
                throw;
            }
        }
    }

    private static async Task DrainErrors(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096]; long total = 0; int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += read;
            if (total > 8 * 1024 * 1024) throw new ModManagerException("Archive diagnostics exceed the limit.");
        }
    }
}
