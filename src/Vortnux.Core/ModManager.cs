using System.Diagnostics;

namespace Vortnux.Core;

public sealed class ModManager(StateStore store, Func<bool>? gameRunning = null)
{
    public StateStore Store => store;
    public StoreState State => store.Load();
    public ArchiveStager Stager { get; } = new(store.DirectoryPath);
    public Action<int>? AfterWrite { get; set; }

    public static bool IsCyberpunkRunning()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.ProcessName.Contains("Cyberpunk2077", StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { /* Process exited between enumeration and name lookup. */ }
            }
        }
        return false;
    }

    public GameInstallation AddGame(string root)
    {
        using var operationLock = store.AcquireLock();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
            SafeFiles.RejectLink(ancestor.FullName);
        if (!File.Exists(SafeFiles.Resolve(root, "bin/x64/Cyberpunk2077.exe")))
            throw new ModManagerException("Select the Cyberpunk directory containing bin/x64/Cyberpunk2077.exe.");
        if (root.StartsWith(store.DirectoryPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            store.DirectoryPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || root == store.DirectoryPath)
            throw new ModManagerException("Game and application state directories must be separate.");
        var state = store.Load();
        if (state.Games.FirstOrDefault(game => game.Root == root) is { } existing) return existing;
        var game = new GameInstallation(Guid.NewGuid().ToString("N"), "Cyberpunk 2077", root);
        store.Save(state with { Games = [.. state.Games, game] });
        return game;
    }

    public Activity Install(ManagedMod mod, bool allowConflicts = false)
        => InstallBatch([mod], mod.GameId, "Mod installed", mod.Name, mod.Id, allowConflicts);

    public Activity InstallBatch(List<ManagedMod> mods, string gameId, string title, string name, string entityId, bool allowConflicts = false)
    {
        if (mods.Count == 0 || mods.Any(mod => mod.GameId != gameId)) throw new ModManagerException("Invalid installation batch.");
        return Change(gameId, state =>
        {
            if (mods.Any(mod => state.Mods.Any(existing => existing.Id == mod.Id))) throw new ModManagerException("This mod is already installed.");
            var occupied = Winners(state.Mods.Where(mod => mod.GameId == gameId));
            var claimed = new HashSet<string>(occupied.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var file in mods.SelectMany(mod => mod.Files))
                if (!claimed.Add(file.Path) && !allowConflicts)
                    throw new ModManagerException("File conflict: " + file.Path + ". Review and enable replacement before installing.");
            return [.. state.Mods, .. mods];
        }, title, name, new Destination(mods[0].CollectionId is null ? "Mods" : "Collections", gameId, entityId));
    }

    public Activity Remove(string modId)
    {
        var mod = State.Mods.FirstOrDefault(mod => mod.Id == modId) ?? throw new ModManagerException("Mod no longer exists.");
        return Change(mod.GameId, state => state.Mods.Where(item => item.Id != modId).ToList(),
            "Mod uninstalled", mod.Name, new Destination("Activity", mod.GameId, modId));
    }

    public Activity RemoveCollection(string gameId, string collectionId)
        => Change(gameId, state => state.Mods.Where(mod => mod.GameId != gameId || mod.CollectionId != collectionId).ToList(),
            "Collection uninstalled", collectionId, new Destination("Activity", gameId, collectionId));

    public Activity Toggle(string modId)
    {
        var mod = State.Mods.FirstOrDefault(mod => mod.Id == modId) ?? throw new ModManagerException("Mod no longer exists.");
        return Change(mod.GameId, state => state.Mods.Select(item => item.Id == modId ? item with { Enabled = !item.Enabled } : item).ToList(),
            mod.Enabled ? "Mod disabled" : "Mod enabled", mod.Name, new Destination("Mods", mod.GameId, mod.Id));
    }

    private Activity Change(string gameId, Func<StoreState, List<ManagedMod>> transform, string title, string name, Destination target)
    {
        using var operationLock = store.AcquireLock();
        if ((gameRunning ?? IsCyberpunkRunning)()) throw new ModManagerException("Close Cyberpunk before changing deployed mods. Downloads can continue.");
        RecoverLocked();
        var state = store.Load();
        var game = state.Games.FirstOrDefault(game => game.Id == gameId) ?? throw new ModManagerException("Select a managed game first.");
        var nextMods = transform(state);
        var before = Winners(state.Mods.Where(mod => mod.GameId == gameId));
        var after = Winners(nextMods.Where(mod => mod.GameId == gameId));
        var originals = new List<OriginalFile>(state.Originals);
        var paths = before.Keys.Concat(after.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
        var mutations = new List<FileMutation>();
        foreach (var relative in paths)
        {
            var destination = SafeFiles.Resolve(game.Root, relative);
            var currentHash = File.Exists(destination) ? SafeFiles.Hash(destination) : null;
            var original = originals.FirstOrDefault(file => file.GameId == gameId && file.Path == relative);
            if (original is null)
            {
                original = new OriginalFile(gameId, relative, currentHash, currentHash is null ? null : Preserve(destination));
                originals.Add(original);
            }
            var expected = before.TryGetValue(relative, out var previous) ? previous.Hash : original.Hash;
            if (currentHash != expected) throw new ModManagerException("File changed outside Vortnux: " + relative + ". Resolve this change before proceeding.");
            var next = after.GetValueOrDefault(relative);
            var nextHash = next?.Hash ?? original.Hash;
            if (currentHash == nextHash) continue;
            var nextBlob = next?.Blob ?? original.Blob;
            if (nextBlob is not null && SafeFiles.Hash(nextBlob) != nextHash) throw new ModManagerException("Staging cache is corrupted.");
            mutations.Add(new FileMutation(relative, currentHash, currentHash is null ? null : Preserve(destination), nextHash, nextBlob));
        }
        var nextOriginals = originals.Where(file => file.GameId != gameId || nextMods.Any(mod => mod.GameId == gameId && mod.Files.Any(item => item.Path == file.Path))).ToList();
        var journal = new DeploymentJournal(gameId, mutations, nextMods, nextOriginals);
        store.Save(state with { Journal = journal });
        // An interrupted operation retains the journal. Startup reverts it before accepting new work.
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            if ((gameRunning ?? IsCyberpunkRunning)()) throw new ModManagerException("Cyberpunk started during deployment. Recovery is required after it closes.");
            var destination = SafeFiles.Resolve(game.Root, mutation.Path);
            if ((File.Exists(destination) ? SafeFiles.Hash(destination) : null) != mutation.BeforeHash)
                throw new ModManagerException("Game file changed during deployment: " + mutation.Path);
            Apply(destination, mutation.AfterBlob);
            if ((File.Exists(destination) ? SafeFiles.Hash(destination) : null) != mutation.AfterHash)
                throw new ModManagerException("Deployment verification failed: " + mutation.Path);
            AfterWrite?.Invoke(index);
        }
        var activity = new Activity(Guid.NewGuid().ToString("N"), title, name, target, DateTimeOffset.UtcNow);
        store.Save(state with { Mods = nextMods, Originals = nextOriginals, Journal = null,
            Activities = state.Activities.Append(activity).TakeLast(200).ToList() });
        return activity;
    }

    public void Recover()
    {
        using var operationLock = store.AcquireLock();
        RecoverLocked();
    }

    private void RecoverLocked()
    {
        var state = store.Load();
        if (state.Journal is not { } journal) return;
        if ((gameRunning ?? IsCyberpunkRunning)()) throw new ModManagerException("Close Cyberpunk to recover the interrupted deployment.");
        var game = state.Games.Single(game => game.Id == journal.GameId);
        // Validate every destination before reverting any. Never overwrite an unrelated external edit.
        foreach (var mutation in journal.Files)
        {
            var file = SafeFiles.Resolve(game.Root, mutation.Path);
            var hash = File.Exists(file) ? SafeFiles.Hash(file) : null;
            if (hash != mutation.BeforeHash && hash != mutation.AfterHash)
                throw new ModManagerException("Recovery blocked by external changes: " + mutation.Path);
            if (mutation.BeforeBlob is not null && SafeFiles.Hash(mutation.BeforeBlob) != mutation.BeforeHash)
                throw new ModManagerException("Recovery backup is corrupted: " + mutation.Path);
        }
        foreach (var mutation in journal.Files.AsEnumerable().Reverse())
            Apply(SafeFiles.Resolve(game.Root, mutation.Path), mutation.BeforeBlob);
        store.Save(state with { Journal = null });
    }

    public void MarkRead(string id)
    {
        using var operationLock = store.AcquireLock();
        var state = store.Load();
        store.Save(state with { Activities = state.Activities.Select(item => item.Id == id ? item with { IsRead = true } : item).ToList() });
    }

    public void Record(Activity activity)
    {
        using var operationLock = store.AcquireLock();
        var state = store.Load();
        store.Save(state with { Activities = state.Activities.Append(activity).TakeLast(200).ToList() });
    }

    private string Preserve(string source)
    {
        var hash = SafeFiles.Hash(source);
        var directory = Path.Combine(store.DirectoryPath, "blobs");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, hash);
        if (!File.Exists(destination)) SafeFiles.AtomicCopy(source, destination);
        else if (SafeFiles.Hash(destination) != hash) throw new ModManagerException("Backup cache is corrupted.");
        return destination;
    }

    private static void Apply(string destination, string? blob)
    {
        if (blob is null) { if (File.Exists(destination)) File.Delete(destination); }
        else SafeFiles.AtomicCopy(blob, destination);
    }

    private static Dictionary<string, ModFile> Winners(IEnumerable<ManagedMod> mods)
    {
        var result = new Dictionary<string, ModFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods.Where(mod => mod.Enabled))
            foreach (var file in mod.Files) result[file.Path] = file;
        return result;
    }
}
