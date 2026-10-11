using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Vortnux.Core;

public interface INexusCredentialStore
{
    string Description { get; }
    Task<string?> LoadAsync(CancellationToken token = default);
    Task SaveAsync(string key, CancellationToken token = default);
    Task ForgetAsync(CancellationToken token = default);
}

public interface ISecretTool
{
    Task<string?> RunAsync(string operation, string profile, string? secret, CancellationToken token);
}

/// <summary>Secrets never enter process arguments, SQLite, activity records or database backups.</summary>
public sealed class NexusCredentialStore(string stateDirectory, ISecretTool? secretTool = null) : INexusCredentialStore
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string directory = Path.Combine(Path.GetFullPath(stateDirectory), "credentials");
    private readonly string profile = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(stateDirectory))));
    private readonly ISecretTool secrets = secretTool ?? new SecretTool();
    public string Description { get; private set; } = "API keys are saved only in the Linux keyring. An unlocked Secret Service keyring and secret-tool are required.";
    private string Marker => Path.Combine(directory, "storage");
    private string KeyFile => Path.Combine(directory, "nexus-api-key");

    public async Task<string?> LoadAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(directory)) return null;
        ProtectDirectory();
        if (File.Exists(KeyFile) || new FileInfo(KeyFile).LinkTarget is not null)
        {
            CheckFile(KeyFile);
            var legacy = await File.ReadAllTextAsync(KeyFile, token);
            Validate(legacy);
            // Preserve the old file if storing or verifying fails; never use it
            // as a fallback connection when the keyring is unavailable.
            await SaveAsync(legacy, token);
            return legacy;
        }
        if (!File.Exists(Marker)) return null;
        CheckFile(Marker);
        if (await File.ReadAllTextAsync(Marker, token) != "keyring")
            throw new ModManagerException("Saved API-key storage is incomplete. Connect your key in Settings to save it in the Linux keyring.");
        var key = await secrets.RunAsync("lookup", profile, null, token);
        if (key is null)
            throw new ModManagerException("The saved Nexus key could not be loaded from the Linux keyring. Unlock your keyring and click Retry saved connection in Settings.");
        Validate(key);
        Description = "API key saved only in the Linux keyring.";
        return key;
    }

    public async Task SaveAsync(string key, CancellationToken token = default)
    {
        Validate(key);
        ProtectDirectory();
        if (File.Exists(KeyFile) || new FileInfo(KeyFile).LinkTarget is not null) CheckFile(KeyFile);
        if (await secrets.RunAsync("store", profile, key, token) is null ||
            !string.Equals(await secrets.RunAsync("lookup", profile, null, token), key, StringComparison.Ordinal))
            throw new ModManagerException("Could not save and verify the API key in the Linux keyring. Install libsecret (secret-tool), start/unlock a Secret Service keyring and retry. No local key file was created; any existing file is retained until migration succeeds.");
        await WritePrivateAsync(Marker, "keyring", token);
        if (File.Exists(KeyFile)) File.Delete(KeyFile);
        Description = "API key saved only in the Linux keyring.";
    }

    public async Task ForgetAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(directory)) return;
        ProtectDirectory();
        if (File.Exists(Marker))
        {
            CheckFile(Marker);
            if (await File.ReadAllTextAsync(Marker, token) == "keyring")
            {
                // clear only removes unlocked items; lookup requests unlocking first.
                if (await secrets.RunAsync("lookup", profile, null, token) is null ||
                    await secrets.RunAsync("clear", profile, null, token) is null)
                    throw new ModManagerException("Could not remove the saved key from the Linux keyring. Unlock your keyring and try again.");
            }
        }
        if (File.Exists(KeyFile)) { CheckFile(KeyFile); File.Delete(KeyFile); }
        File.Delete(Marker);
        Description = "No API key saved.";
    }

    private void ProtectDirectory()
    {
        if (!OperatingSystem.IsLinux()) throw new ModManagerException("Persistent API-key storage currently requires Linux.");
        if (new DirectoryInfo(directory).LinkTarget is not null) throw new ModManagerException("Credential directory must not be a symbolic link.");
        Directory.CreateDirectory(directory, DirectoryMode);
        File.SetUnixFileMode(directory, DirectoryMode);
    }

    private static void CheckFile(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new ModManagerException("Credential files must not be symbolic links.");
        if (new FileInfo(path).Length > 8192) throw new ModManagerException("Saved API-key file is too large.");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, FileMode);
    }

    private static async Task WritePrivateAsync(string path, string value, CancellationToken token)
    {
        if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) CheckFile(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = FileMode;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            throw new ModManagerException("API key must be a nonempty single line, at most 4096 characters.");
    }
}

public sealed class SecretTool(string executable = "secret-tool") : ISecretTool
{
    public async Task<string?> RunAsync(string operation, string profile, string? secret, CancellationToken token)
    {
        if (operation is not ("store" or "lookup" or "clear")) throw new ArgumentException("Unknown secret-tool operation.", nameof(operation));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        } };
        process.StartInfo.ArgumentList.Add(operation);
        if (operation == "store") process.StartInfo.ArgumentList.Add("--label=Vortnux Nexus API key");
        foreach (var argument in new[] { "application", "io.vortnux.Vortnux", "service", "nexusmods", "profile", profile })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
            // secret-tool stores every stdin byte, including any trailing newline.
            if (secret is not null) await process.StandardInput.WriteAsync(secret.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            var result = await output; await errors;
            return process.ExitCode == 0 ? result.TrimEnd('\r', '\n') : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (IOException) { return null; }
        finally
        {
            try { if (process.Id != 0 && !process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[8193]; var length = 0;
        while (length < buffer.Length)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(length), token);
            if (count == 0) return new string(buffer, 0, length);
            length += count;
        }
        throw new IOException("Secret-tool response exceeded the limit.");
    }
}
