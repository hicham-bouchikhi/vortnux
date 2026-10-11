using System.Diagnostics;
using System.Text.Json;
using Vortnux.Core;

namespace Vortnux.Tests;

public sealed class ArchiveTests
{
    private static string Pack(Fixture fixture, params (string Name, string Content)[] files)
    {
        var input = Path.Combine(fixture.Root, "sevenzip-input"); Directory.CreateDirectory(input);
        foreach (var file in files) File.WriteAllText(Path.Combine(input, file.Name), file.Content);
        var archive = Path.Combine(fixture.Root, "fixture.7z");
        var start = new ProcessStartInfo("7z") { WorkingDirectory = input, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("a"); start.ArgumentList.Add("-t7z"); start.ArgumentList.Add(archive);
        foreach (var file in files) start.ArgumentList.Add(file.Name);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("7-Zip fixture creation timed out."); }
        Assert.Equal(0, process.ExitCode); return archive;
    }

    [Fact]
    public void SevenZipModInstallsAndUninstallsThroughSameOwnershipJournal()
    {
        using var fixture = new Fixture(); var archive = Pack(fixture, ("a.archive", "fixture"));
        var mod = fixture.Manager.Stager.Stage(archive, fixture.Game.Id); fixture.Manager.Install(mod);
        Assert.Equal("fixture", File.ReadAllText(fixture.File("archive/pc/mod/a.archive")));
        fixture.Manager.Remove(mod.Id); Assert.False(File.Exists(fixture.File("archive/pc/mod/a.archive")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Manager.Store.DirectoryPath, "temporary-archives")));
    }

    [Fact]
    public void CollectionManifestCanBeReadFromRealSevenZipBundle()
    {
        using var fixture = new Fixture(); var archive = Pack(fixture, ("collection.json", ApiTests.Manifest));
        var entry = Assert.Single(SevenZip.Inventory(archive)); Assert.Equal("collection.json", entry.Path);
        using var output = new MemoryStream(); SevenZip.Extract(archive, entry, output, 8 * 1024 * 1024);
        using var document = JsonDocument.Parse(output.ToArray()); Assert.Single(Collections.Parse(document.RootElement).Entries);
    }

    [Fact]
    public void ExtractionLimitAndTraversalStopBeforeAnyGameWrite()
    {
        using var fixture = new Fixture(); var archive = Pack(fixture, ("a.archive", "fixture"));
        var entry = Assert.Single(SevenZip.Inventory(archive)); using var output = new MemoryStream();
        Assert.Throws<ModManagerException>(() => SevenZip.Extract(archive, entry, output, 1));
        var unsafeArchive = fixture.Zip(("../escape", "fixture"));
        Assert.Throws<ModManagerException>(() => SevenZip.Inventory(unsafeArchive));
        Assert.Empty(fixture.Manager.State.Mods);
    }

    [Fact]
    public void ZipCentralDirectoryChecksumMustMatchActualContent()
    {
        using var fixture = new Fixture(); var zip = fixture.Zip(("a.archive", "fixture"));
        var bytes = File.ReadAllBytes(zip);
        for (var index = 0; index < bytes.Length - 20; index++)
            if (bytes[index] == 0x50 && bytes[index + 1] == 0x4b && bytes[index + 2] == 1 && bytes[index + 3] == 2)
            { bytes[index + 16] ^= 1; break; }
        File.WriteAllBytes(zip, bytes);
        Assert.Throws<ModManagerException>(() => fixture.Manager.Stager.Stage(zip, fixture.Game.Id));
    }

    [Theory]
    [InlineData("archive/pc/mod/Foo/a.archive", "archive/pc/mod/foo/b.archive")]
    [InlineData("archive/pc/mod/a.archive", "archive/pc/mod/a.archive/b.archive")]
    public void ParentPathCollisionsFailDuringStaging(string first, string second)
    {
        using var fixture = new Fixture(); var zip = fixture.Zip((first, "first"), (second, "second"));
        Assert.Throws<ModManagerException>(() => fixture.Manager.Stager.Stage(zip, fixture.Game.Id));
        Assert.Null(fixture.Manager.State.Journal);
    }
}
