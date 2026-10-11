using System.IO.Compression;
using Vortnux.Core;
using System.Text.Json;
using Vortnux.Core.Generated;
using System.Security.Cryptography;

namespace Vortnux.Desktop;

public static class SmokeTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "vortnux-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gameRoot = Path.Combine(root, "game");
            Directory.CreateDirectory(Path.Combine(gameRoot, "bin", "x64"));
            File.WriteAllText(Path.Combine(gameRoot, "bin", "x64", "Cyberpunk2077.exe"), "fixture");
            var manager = new ModManager(new StateStore(Path.Combine(root, "state")), () => false);
            var game = manager.AddGame(gameRoot);
            var zip = Path.Combine(root, "mod.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry("example.archive").Open())) writer.Write("mod fixture");
            var mod = manager.Stager.Stage(zip, game.Id);
            manager.Install(mod);
            var target = Path.Combine(gameRoot, "archive", "pc", "mod", "example.archive");
            if (File.ReadAllText(target) != "mod fixture") throw new InvalidOperationException("Install failed.");
            manager.Toggle(mod.Id);
            if (File.Exists(target)) throw new InvalidOperationException("Disable failed.");
            manager.Toggle(mod.Id);
            manager.Remove(mod.Id);
            if (File.Exists(target) || manager.State.Mods.Count != 0) throw new InvalidOperationException("Uninstall failed.");
            if (JsonSerializer.Deserialize("{\"mod_files\":[]}", NexusV3Json.Default.ModFilesResponse) is null)
                throw new InvalidOperationException("Generated client metadata failed.");
            foreach (var fixture in NexusV3Contracts.Fixtures)
                if (JsonSerializer.Deserialize(fixture.Json, fixture.Type, NexusV3Json.Default) is null)
                    throw new InvalidOperationException("Generated DTO metadata failed: " + fixture.Type.Name);
            var catalog = JsonSerializer.Deserialize("{\"nodes\":[{\"name\":\"Fixture\",\"modId\":1,\"game\":{\"domainName\":\"cyberpunk2077\"}}],\"totalCount\":1}", CatalogJson.Default.CatalogWirePage);
            if (catalog?.Nodes.Single().ModId != 1) throw new InvalidOperationException("Catalog metadata failed.");
            using var signingKey = RSA.Create(2048);
            var manifest = new UpdateManifest("1.0.0", "https://github.com/fixture/repo/releases/download/v1/a.AppImage", SafeFiles.Hash(zip), new FileInfo(zip).Length);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(manifest, AppJson.Default.UpdateManifest);
            var signature = signingKey.SignData(metadata, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            Updates.VerifyArtifact(zip, Updates.Verify(metadata, signature, signingKey.ExportSubjectPublicKeyInfoPem()));
            Console.WriteLine("PASS: Native executable, SQLite, ZIP, install, disable, enable, uninstall.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
