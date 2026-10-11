using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vortnux.Core;

public enum CatalogKind { Mods, Collections }
public enum CatalogSort { Recent, Updated, Downloads, Endorsements }
public sealed record CatalogQuery(CatalogKind Kind = CatalogKind.Mods, string Text = "",
    CatalogSort Sort = CatalogSort.Recent, int Offset = 0, int Count = 24);
public sealed record CatalogItem(CatalogKind Kind, string Name, string Summary, string Author,
    long ModId, string? Slug, long Downloads, long Endorsements, string? ImageUrl = null)
{
    public string Url => Kind == CatalogKind.Mods
        ? $"https://www.nexusmods.com/cyberpunk2077/mods/{ModId}"
        : $"https://www.nexusmods.com/games/cyberpunk2077/collections/{Slug}";
}
public sealed record CatalogPage(IReadOnlyList<CatalogItem> Items, int Total);
public interface INexusCatalog
{
    Task<CatalogPage> SearchAsync(CatalogQuery query, CancellationToken cancellationToken = default);
}

// Transport types contain only the selected fields; required properties reject partial responses.
public sealed class CatalogWirePage
{
    [JsonRequired] public List<CatalogWireItem> Nodes { get; set; } = [];
    [JsonRequired] public int TotalCount { get; set; }
}
public sealed class CatalogWireItem
{
    [JsonRequired] public string Name { get; set; } = "";
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public CatalogWireUser? User { get; set; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public long ModId { get; set; }
    public string? Slug { get; set; }
    public long Downloads { get; set; }
    public long Endorsements { get; set; }
    public string? ThumbnailUrl { get; set; }
    public CatalogWireImage? TileImage { get; set; }
    [JsonRequired] public CatalogWireGame Game { get; set; } = new();
}
public sealed class CatalogWireUser { public string? Name { get; set; } }
public sealed class CatalogWireImage { public string? Url { get; set; } }
public sealed class CatalogWireGame { [JsonRequired] public string DomainName { get; set; } = ""; }
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogWirePage))]
public partial class CatalogJson : JsonSerializerContext;

public sealed class NexusCatalog(NexusApi api) : INexusCatalog
{
    public async Task<CatalogPage> SearchAsync(CatalogQuery query, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(query.Kind) || !Enum.IsDefined(query.Sort) || query.Text.Length > 200 ||
            query.Offset < 0 || query.Offset > 1_000_000 || query.Count is < 1 or > 48)
            throw new ModManagerException("Invalid search or page size. Search text must be at most 200 characters.");
        var mods = query.Kind == CatalogKind.Mods;
        var field = mods ? "mods" : "collectionsV2";
        var filterType = mods ? "ModsFilter" : "CollectionsSearchFilter";
        var sortType = mods ? "ModsSort" : "CollectionsSearchSort";
        var visibility = mods ? "viewUploaderHidden:false,viewUserBlockedContent:false," : "viewUserBlockedContent:false,";
        var fields = mods ? "modId author downloads thumbnailUrl" : "slug user{name} downloads:totalDownloads tileImage{url}";
        var graphql = $"query VortnuxBrowse($filter:{filterType},$sort:[{sortType}!],$offset:Int,$count:Int){{{field}({visibility}filter:$filter,sort:$sort,offset:$offset,count:$count){{nodes{{name summary endorsements game{{domainName}} {fields}}} totalCount}}}}";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("query", graphql); writer.WriteStartObject("variables");
            writer.WriteNumber("offset", query.Offset); writer.WriteNumber("count", query.Count);
            writer.WriteStartObject("filter"); writer.WriteString("op", "AND");
            WriteFilter(writer, mods ? "gameDomainName" : "gameDomain", "cyberpunk2077", "EQUALS");
            if (!string.IsNullOrWhiteSpace(query.Text)) WriteFilter(writer, "name", query.Text.Trim(), "WILDCARD");
            if (!mods)
            {
                WriteFilter(writer, "collectionStatus", "listed", "EQUALS");
                writer.WriteStartArray("hasPublishedRevision"); writer.WriteStartObject(); writer.WriteBoolean("value", true);
                writer.WriteString("op", "EQUALS"); writer.WriteEndObject(); writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.WriteStartArray("sort"); writer.WriteStartObject();
            writer.WriteStartObject(query.Sort switch { CatalogSort.Updated => "updatedAt", CatalogSort.Downloads => "downloads", CatalogSort.Endorsements => "endorsements", _ => "createdAt" });
            writer.WriteString("direction", "DESC"); writer.WriteEndObject(); writer.WriteEndObject(); writer.WriteEndArray();
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        using var body = JsonDocument.Parse(buffer.ToArray());
        var response = await api.RequestAsync(HttpMethod.Post, "v2/graphql", body.RootElement, cancellationToken);
        try
        {
            var result = response.GetProperty("data").GetProperty(field).Deserialize(CatalogJson.Default.CatalogWirePage)
                ?? throw new JsonException();
            if (result.Nodes is null || result.TotalCount < 0 || result.Nodes.Count > query.Count || result.TotalCount < result.Nodes.Count)
                throw new JsonException();
            var items = result.Nodes.Select(item =>
            {
                if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Game?.DomainName != "cyberpunk2077" ||
                    item.Downloads < 0 || item.Endorsements < 0 ||
                    (mods ? item.ModId <= 0 : string.IsNullOrWhiteSpace(item.Slug) || item.Slug.Length > 128 || !item.Slug.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
                    throw new JsonException();
                return new CatalogItem(query.Kind, item.Name, item.Summary ?? "", item.Author ?? item.User?.Name ?? "Unknown creator",
                    item.ModId, item.Slug, item.Downloads, item.Endorsements, item.ThumbnailUrl ?? item.TileImage?.Url);
            }).ToArray();
            if (items.Select(item => item.Kind == CatalogKind.Mods ? item.ModId.ToString(System.Globalization.CultureInfo.InvariantCulture) : item.Slug).Distinct().Count() != items.Length)
                throw new JsonException();
            return new CatalogPage(items, result.TotalCount);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ModManagerException("Nexus returned an incomplete or invalid search response. Retry the search."); }
    }

    private static void WriteFilter(Utf8JsonWriter writer, string field, string value, string op)
    {
        writer.WriteStartArray(field); writer.WriteStartObject(); writer.WriteString("value", value);
        writer.WriteString("op", op); writer.WriteEndObject(); writer.WriteEndArray();
    }
}

// Each connected account owns a session. Superseded requests cannot change visible results.
public sealed class CatalogSession(INexusCatalog catalog) : IDisposable
{
    private readonly Dictionary<CatalogQuery, (CatalogPage Page, DateTimeOffset Time)> cache = [];
    private CancellationTokenSource? pending;
    private int generation;
    private bool disposed;
    public CatalogQuery Query { get; private set; } = new();
    public CatalogPage? Result { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public async Task SearchAsync(CatalogQuery query, bool refresh = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Cancel();
        Query = query; Result = null; Error = null;
        var current = generation;
        if (!refresh && cache.TryGetValue(query, out var saved) && DateTimeOffset.UtcNow - saved.Time < TimeSpan.FromMinutes(2))
        { Result = saved.Page; Changed?.Invoke(); return; }
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pending = request; Loading = true; Changed?.Invoke();
        try
        {
            var result = await catalog.SearchAsync(query, request.Token);
            if (disposed || generation != current) return;
            Result = result;
            if (cache.Count >= 12) cache.Remove(cache.MinBy(entry => entry.Value.Time).Key);
            cache[query] = (result, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        { if (!disposed && generation == current) Error = "Search cancelled or timed out. Retry when ready."; }
        catch (Exception error)
        { if (!disposed && generation == current) Error = error is ModManagerException ? error.Message : "Search failed. Check your connection and retry."; }
        finally
        {
            if (!disposed && generation == current)
            { pending = null; Loading = false; Changed?.Invoke(); }
        }
    }

    public void Cancel()
    { generation++; pending?.Cancel(); pending = null; Loading = false; }
    public void Dispose()
    { if (disposed) return; disposed = true; Cancel(); cache.Clear(); Result = null; Changed = null; }
}
