using System.Text.Json.Serialization;

namespace Vortnux.Core;

public sealed record GameInstallation(string Id, string Name, string Root);
public sealed record ModFile(string Path, string Hash, string Blob);
public sealed record ManagedMod(string Id, string GameId, string Name, bool Enabled,
    List<ModFile> Files, string? CollectionId = null);
public sealed record OriginalFile(string GameId, string Path, string? Hash, string? Blob);
public sealed record FileMutation(string Path, string? BeforeHash, string? BeforeBlob,
    string? AfterHash, string? AfterBlob);
public sealed record DeploymentJournal(string GameId, List<FileMutation> Files,
    List<ManagedMod> NextMods, List<OriginalFile> NextOriginals);
public sealed record Destination(string Page, string GameId, string EntityId);
public sealed record Activity(string Id, string Title, string Message, Destination Target,
    DateTimeOffset Timestamp, bool IsRead = false, bool IsError = false);
public sealed record StoreState
{
    public int Schema { get; init; } = 1;
    public List<GameInstallation> Games { get; init; } = [];
    public List<ManagedMod> Mods { get; init; } = [];
    public List<OriginalFile> Originals { get; init; } = [];
    public List<Activity> Activities { get; init; } = [];
    public DeploymentJournal? Journal { get; init; }
}

[JsonSerializable(typeof(StoreState))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(ReleaseTrust))]
[JsonSerializable(typeof(UpdateTransaction))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
public partial class AppJson : JsonSerializerContext;

public sealed class ModManagerException(string message) : Exception(message);
