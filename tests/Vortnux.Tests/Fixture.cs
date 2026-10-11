using System.IO.Compression;
using Vortnux.Core;

namespace Vortnux.Tests;

public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "vortnux-test-" + Guid.NewGuid().ToString("N"));
    public string GameRoot => Path.Combine(Root, "game");
    public ModManager Manager { get; }
    public GameInstallation Game { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Path.Combine(GameRoot, "bin", "x64"));
        System.IO.File.WriteAllText(Path.Combine(GameRoot, "bin", "x64", "Cyberpunk2077.exe"), "fixture");
        Manager = new ModManager(new StateStore(Path.Combine(Root, "state")), () => false);
        Game = Manager.AddGame(GameRoot);
    }
    public string File(string path) => SafeFiles.Resolve(GameRoot, path);
    public void Original(string path, string content)
    {
        var target = File(path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        System.IO.File.WriteAllText(target, content);
    }
    public string Zip(params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Path).Open()); writer.Write(entry.Content);
        }
        return path;
    }
    public ManagedMod Stage(string content = "mod", string? collection = null)
        => Manager.Stager.Stage(Zip(("a.archive", content)), Game.Id, "Example", collection);
    public void Dispose() { Directory.Delete(Root, true); }
}
