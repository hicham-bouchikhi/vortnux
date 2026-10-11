using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace Vortnux.Core;

public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size);
public sealed record ReleaseTrust(string Repository, string PublicKey);
public sealed record UpdateTransaction(string Current, string Previous, string Replacement, string Backup,
    string Marker, string BeforeHash, string AfterHash);

public static class Updates
{
    public static UpdateManifest Verify(byte[] manifest, byte[] signature, string publicKey)
    {
        using var key = RSA.Create();
        key.ImportFromPem(publicKey);
        if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new ModManagerException("Release signature is invalid.");
        var result = JsonSerializer.Deserialize(manifest, AppJson.Default.UpdateManifest)
            ?? throw new ModManagerException("Empty release manifest.");
        if (!Version.TryParse(result.Version, out _) || result.Size <= 0 || result.Size > 1024L * 1024 * 1024 ||
            result.Sha256.Length != 64 || result.Sha256.Any(c => !Uri.IsHexDigit(c)) ||
            !Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            !uri.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal))
            throw new ModManagerException("Invalid release manifest.");
        return result;
    }

    public static void VerifyArtifact(string path, UpdateManifest manifest)
    {
        if (new FileInfo(path).Length != manifest.Size ||
            !string.Equals(SafeFiles.Hash(path), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new ModManagerException("Release artifact verification failed.");
    }

    public static async Task ApplyAppImageAsync(string downloaded, string current, UpdateManifest manifest,
        StateStore store, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) throw new ModManagerException("AppImage updates require Linux.");
        current = Path.GetFullPath(current);
        if (!current.EndsWith(".AppImage", StringComparison.Ordinal) || !File.Exists(current))
            throw new ModManagerException("Only a launched AppImage can replace itself. Use your package manager for an Arch installation.");
        for (var ancestor = new DirectoryInfo(Path.GetDirectoryName(current)!); ancestor is not null; ancestor = ancestor.Parent)
            SafeFiles.RejectLink(ancestor.FullName);
        SafeFiles.RejectLink(current);
        VerifyArtifact(downloaded, manifest);
        var replacement = current + ".pending-" + Guid.NewGuid().ToString("N");
        var previous = current + ".previous";
        var marker = Path.Combine(store.DirectoryPath, "health-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(store.DirectoryPath, "before-update-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        var replaced = false;
        var transactionPath = Path.Combine(store.DirectoryPath, "update-transaction.json");
        Process? launched = null;
        FileStream? updateLock = null;
        try
        {
            SafeFiles.AtomicCopy(downloaded, replacement);
            File.SetUnixFileMode(replacement, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Run the downloaded native payload before touching the existing executable or database.
            var probeInfo = new ProcessStartInfo(replacement) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            probeInfo.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";
            probeInfo.ArgumentList.Add("--smoke-test");
            using (var probe = Process.Start(probeInfo) ?? throw new ModManagerException("Cannot start release verification."))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try { await probe.WaitForExitAsync(deadline.Token); }
                catch { if (!probe.HasExited) probe.Kill(true); throw; }
                if (probe.ExitCode != 0) throw new ModManagerException("New release failed its native smoke test.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            updateLock = store.AcquireLock();
            {
                if (store.Load().Journal is not null) throw new ModManagerException("Recover the pending deployment before updating.");
                store.Backup(backup);
                SafeFiles.RejectLink(previous);
                SafeFiles.AtomicCopy(current, previous);
                File.SetUnixFileMode(previous, File.GetUnixFileMode(current));
                var transaction = new UpdateTransaction(current, previous, replacement, backup, marker, SafeFiles.Hash(current), manifest.Sha256.ToLowerInvariant());
                var transactionTemporary = transactionPath + ".tmp";
                using (var stream = new FileStream(transactionTemporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, transaction, AppJson.Default.UpdateTransaction); stream.Flush(true);
                }
                File.Move(transactionTemporary, transactionPath, true);
                File.Move(replacement, current, true); replaced = true;
            }
            var start = new ProcessStartInfo(current) { UseShellExecute = false };
            start.Environment["APPIMAGE_EXTRACT_AND_RUN"] = "1";
            start.ArgumentList.Add("--update-health-check"); start.ArgumentList.Add(marker);
            launched = Process.Start(start) ?? throw new ModManagerException("Cannot launch the updated application.");
            var timeout = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!File.Exists(marker))
            {
                if (launched.HasExited || DateTimeOffset.UtcNow > timeout) throw new ModManagerException("Updated GUI failed its startup health check. Previous executable restored.");
                await Task.Delay(100, cancellationToken);
            }
            File.Delete(transactionPath);
        }
        catch
        {
            if (launched is not null && !launched.HasExited)
            {
                launched.Kill(true);
                await launched.WaitForExitAsync(CancellationToken.None);
            }
            if (replaced)
            {
                File.Move(previous, current, true);
                store.RestoreBackup(backup);
            }
            if (File.Exists(transactionPath)) File.Delete(transactionPath);
            throw;
        }
        finally
        {
            launched?.Dispose();
            updateLock?.Dispose();
            if (File.Exists(marker)) File.Delete(marker);
            if (File.Exists(replacement)) File.Delete(replacement);
        }
    }

    public static bool RecoverAppImageUpdate(StateStore store, string? launchedAppImage)
    {
        var path = Path.Combine(store.DirectoryPath, "update-transaction.json");
        if (!File.Exists(path)) return false;
        using var operationLock = store.AcquireLock();
        var transaction = JsonSerializer.Deserialize(File.ReadAllBytes(path), AppJson.Default.UpdateTransaction)
            ?? throw new ModManagerException("Update recovery journal is unreadable.");
        if (launchedAppImage is null || Path.GetFullPath(launchedAppImage) != transaction.Current)
            throw new ModManagerException("Interrupted AppImage update: relaunch the original AppImage to recover it.");
        SafeFiles.RejectLink(transaction.Current); SafeFiles.RejectLink(transaction.Previous);
        var actual = File.Exists(transaction.Current) ? SafeFiles.Hash(transaction.Current) : null;
        if (actual != transaction.BeforeHash && actual != transaction.AfterHash)
            throw new ModManagerException("Update recovery blocked by an external executable change.");
        if (actual == transaction.AfterHash && File.Exists(transaction.Marker))
        {
            File.Delete(transaction.Marker); File.Delete(path); return false;
        }
        if (actual != transaction.BeforeHash)
        {
            if (!File.Exists(transaction.Previous) || SafeFiles.Hash(transaction.Previous) != transaction.BeforeHash)
                throw new ModManagerException("Update recovery backup is missing or corrupted.");
            File.Move(transaction.Previous, transaction.Current, true);
        }
        store.RestoreBackup(transaction.Backup);
        if (File.Exists(transaction.Replacement)) File.Delete(transaction.Replacement);
        File.Delete(path); return true;
    }
}
