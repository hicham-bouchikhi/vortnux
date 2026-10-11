using System.Text.Json;
using Vortnux.Core;

namespace Vortnux.Tests;

public sealed class UpdateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UpdateChecksHealthAndRestoresOldExecutableOnFailure(bool healthy)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Fixture();
        var current = Path.Combine(fixture.Root, "Current.AppImage"); File.WriteAllText(current, "previous executable");
        File.SetUnixFileMode(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var candidate = Path.Combine(fixture.Root, "Candidate.AppImage");
        File.WriteAllText(candidate, "#!/bin/sh\nif [ \"$1\" = \"--smoke-test\" ]; then exit 0; fi\n" +
            (healthy ? "printf ready > \"$2\"\n" : "exit 1\n"));
        var manifest = new UpdateManifest("1.0.0", "https://github.com/owner/repo/releases/download/v1/a.AppImage", SafeFiles.Hash(candidate), new FileInfo(candidate).Length);
        if (healthy)
        {
            await Updates.ApplyAppImageAsync(candidate, current, manifest, fixture.Manager.Store, TestContext.Current.CancellationToken);
            Assert.Equal(manifest.Sha256, SafeFiles.Hash(current)); Assert.Equal("previous executable", File.ReadAllText(current + ".previous"));
        }
        else
        {
            await Assert.ThrowsAsync<ModManagerException>(() => Updates.ApplyAppImageAsync(candidate, current, manifest, fixture.Manager.Store, TestContext.Current.CancellationToken));
            Assert.Equal("previous executable", File.ReadAllText(current));
        }
        Assert.False(File.Exists(Path.Combine(fixture.Manager.Store.DirectoryPath, "update-transaction.json")));
        Assert.Single(Directory.GetFiles(fixture.Manager.Store.DirectoryPath, "before-update-*.sqlite3"));
    }

    [Fact]
    public void InterruptedUpdateRestoresDatabaseAndExecutableFromJournal()
    {
        using var fixture = new Fixture();
        var current = Path.Combine(fixture.Root, "Current.AppImage"); File.WriteAllText(current, "new");
        var previous = current + ".previous"; File.WriteAllText(previous, "old");
        var backup = Path.Combine(fixture.Manager.Store.DirectoryPath, "backup.sqlite3"); fixture.Manager.Store.Backup(backup);
        fixture.Manager.Install(fixture.Stage()); Assert.Single(fixture.Manager.State.Mods);
        var transaction = new UpdateTransaction(current, previous, current + ".pending", backup,
            Path.Combine(fixture.Manager.Store.DirectoryPath, "health"), SafeFiles.Hash(previous), SafeFiles.Hash(current));
        File.WriteAllBytes(Path.Combine(fixture.Manager.Store.DirectoryPath, "update-transaction.json"), JsonSerializer.SerializeToUtf8Bytes(transaction, AppJson.Default.UpdateTransaction));
        Assert.True(Updates.RecoverAppImageUpdate(fixture.Manager.Store, current));
        Assert.Equal("old", File.ReadAllText(current)); Assert.Empty(fixture.Manager.State.Mods);
        Assert.False(Updates.RecoverAppImageUpdate(fixture.Manager.Store, current));
    }
}
