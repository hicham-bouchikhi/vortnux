using Vortnux.Core;

namespace Vortnux.Tests;

public sealed class DeploymentTests
{
    private const string Target = "archive/pc/mod/a.archive";

    [Fact]
    public void UninstallRestoresOriginalAndDatabasePersists()
    {
        using var fixture = new Fixture(); fixture.Original(Target, "original");
        var mod = fixture.Stage(); fixture.Manager.Install(mod);
        Assert.Equal("mod", File.ReadAllText(fixture.File(Target)));
        var reopened = new ModManager(new StateStore(fixture.Manager.Store.DirectoryPath), () => false);
        Assert.Single(reopened.State.Mods); reopened.Remove(mod.Id);
        Assert.Equal("original", File.ReadAllText(fixture.File(Target)));
        Assert.Empty(reopened.State.Mods); Assert.Empty(reopened.State.Originals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlappingOwnersCanBeRemovedInEitherOrder(bool removeLatest)
    {
        using var fixture = new Fixture(); fixture.Original(Target, "original");
        var first = fixture.Stage("first"); var second = fixture.Stage("second");
        fixture.Manager.Install(first);
        Assert.Throws<ModManagerException>(() => fixture.Manager.Install(second));
        Assert.Equal("first", File.ReadAllText(fixture.File(Target)));
        fixture.Manager.Install(second, true);
        fixture.Manager.Remove(removeLatest ? second.Id : first.Id);
        Assert.Equal(removeLatest ? "first" : "second", File.ReadAllText(fixture.File(Target)));
        fixture.Manager.Remove(removeLatest ? first.Id : second.Id);
        Assert.Equal("original", File.ReadAllText(fixture.File(Target)));
    }

    [Fact]
    public void DisableEnableAndRemoveDisabledPreserveBaseline()
    {
        using var fixture = new Fixture(); var mod = fixture.Stage(); fixture.Manager.Install(mod);
        fixture.Manager.Toggle(mod.Id); Assert.False(File.Exists(fixture.File(Target)));
        fixture.Manager.Toggle(mod.Id); Assert.Equal("mod", File.ReadAllText(fixture.File(Target)));
        fixture.Manager.Toggle(mod.Id); fixture.Manager.Remove(mod.Id);
        Assert.False(File.Exists(fixture.File(Target))); Assert.Empty(fixture.Manager.State.Originals);
    }

    [Fact]
    public void CollectionRemovalPreservesIndependentOwner()
    {
        using var fixture = new Fixture(); var independent = fixture.Stage("independent");
        var member = fixture.Stage("collection", "test@1");
        fixture.Manager.Install(independent); fixture.Manager.Install(member, true);
        fixture.Manager.RemoveCollection(fixture.Game.Id, "test@1");
        Assert.Equal("independent", File.ReadAllText(fixture.File(Target)));
        Assert.Equal(independent.Id, Assert.Single(fixture.Manager.State.Mods).Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CrashRecoveryRevertsEveryFileAndKeepsOldMetadata(int failureIndex)
    {
        using var fixture = new Fixture(); fixture.Original(Target, "original");
        var mod = fixture.Manager.Stager.Stage(fixture.Zip(("a.archive", "new"), ("b.archive", "new")), fixture.Game.Id);
        fixture.Manager.AfterWrite = index => { if (index == failureIndex) throw new IOException("simulated termination"); };
        Assert.Throws<IOException>(() => fixture.Manager.Install(mod));
        Assert.NotNull(fixture.Manager.State.Journal); Assert.Empty(fixture.Manager.State.Mods);
        var reopened = new ModManager(new StateStore(fixture.Manager.Store.DirectoryPath), () => false);
        reopened.Recover(); reopened.Recover();
        Assert.Equal("original", File.ReadAllText(fixture.File(Target)));
        Assert.False(File.Exists(fixture.File("archive/pc/mod/b.archive")));
        Assert.Null(reopened.State.Journal);
    }

    [Fact]
    public void RecoveryChecksAllExternalChangesBeforeWriting()
    {
        using var fixture = new Fixture(); var mod = fixture.Stage();
        fixture.Manager.AfterWrite = _ => throw new IOException("crash");
        Assert.Throws<IOException>(() => fixture.Manager.Install(mod));
        File.WriteAllText(fixture.File(Target), "external");
        Assert.Throws<ModManagerException>(fixture.Manager.Recover);
        Assert.Equal("external", File.ReadAllText(fixture.File(Target)));
        Assert.NotNull(fixture.Manager.State.Journal);
    }

    [Fact]
    public void ExternalEditsAndCorruptedCacheNeverGetOverwritten()
    {
        using var fixture = new Fixture(); var mod = fixture.Stage(); fixture.Manager.Install(mod);
        File.WriteAllText(fixture.File(Target), "external");
        Assert.Throws<ModManagerException>(() => fixture.Manager.Remove(mod.Id));
        Assert.Equal("external", File.ReadAllText(fixture.File(Target)));
        var other = fixture.Manager.Stager.Stage(fixture.Zip(("b.archive", "other")), fixture.Game.Id);
        File.WriteAllText(other.Files[0].Blob, "corrupted");
        Assert.Throws<ModManagerException>(() => fixture.Manager.Install(other));
        Assert.False(File.Exists(fixture.File("archive/pc/mod/b.archive")));
    }

    [Fact]
    public void RunningGameBlocksDeploymentAndRecovery()
    {
        using var fixture = new Fixture(); var mod = fixture.Stage();
        var blocked = new ModManager(fixture.Manager.Store, () => true);
        Assert.Throws<ModManagerException>(() => blocked.Install(mod));
        Assert.False(File.Exists(fixture.File(Target)));
    }

    [Theory]
    [InlineData("../a.archive")]
    [InlineData("/a.archive")]
    [InlineData("C:\\a.archive")]
    [InlineData("outer/fomod/ModuleConfig.xml")]
    [InlineData("unknown.dll")]
    public void UnsupportedOrUnsafeArchivesFailBeforeDeployment(string path)
    {
        using var fixture = new Fixture();
        Assert.Throws<ModManagerException>(() => fixture.Manager.Stager.Stage(fixture.Zip((path, "data")), fixture.Game.Id));
        Assert.Empty(fixture.Manager.State.Mods);
    }

    [Fact]
    public void RejectsCaseAliasesAndSymlinkEscape()
    {
        using var fixture = new Fixture();
        Assert.Throws<ModManagerException>(() => fixture.Manager.Stager.Stage(fixture.Zip(("a.archive", "a"), ("A.archive", "b")), fixture.Game.Id));
        Directory.CreateDirectory(Path.Combine(fixture.GameRoot, "Archive"));
        Assert.Throws<ModManagerException>(() => fixture.Manager.Install(fixture.Stage()));
        Directory.Delete(Path.Combine(fixture.GameRoot, "Archive"));
        var outside = Path.Combine(fixture.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(fixture.GameRoot, "archive"), outside);
        Assert.Throws<ModManagerException>(() => fixture.Manager.Install(fixture.Stage()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void DatabaseBackupRestoresActivitiesAndState()
    {
        using var fixture = new Fixture(); fixture.Manager.Install(fixture.Stage());
        var destination = Path.Combine(fixture.Root, "backup.sqlite3"); fixture.Manager.Store.Backup(destination);
        var copy = Path.Combine(fixture.Root, "copy"); Directory.CreateDirectory(copy);
        File.Copy(destination, Path.Combine(copy, "vortnux.sqlite3"));
        var state = new StateStore(copy).Load(); Assert.Single(state.Mods); Assert.Single(state.Activities);
    }
}
