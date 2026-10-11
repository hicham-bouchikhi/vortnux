using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Vortnux.Core;

public sealed record NexusAccount(long Id, string Name, bool Premium);
public sealed record NexusModLink(string Game, long ModId, long? FileId = null,
    string? Key = null, long? Expires = null, long? UserId = null);
public sealed record NexusCollectionLink(string Game, string Slug, int? Revision);
public sealed record CollectionFile(string Name, long ModId, long FileId, bool Optional);
public sealed record NexusCollection(string Name, string Slug, int Revision, string DownloadLink, List<CollectionFile> Files);

public static class NexusLinks
{
    public static object Parse(string input)
    {
        if (input.Length > 16384 || !Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            throw new ModManagerException("Paste a valid Nexus mod or collection URL.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        var nxm = uri.Scheme == "nxm";
        if (!nxm && (uri.Scheme != "https" || uri.Host is not ("www.nexusmods.com" or "nexusmods.com" or "next.nexusmods.com")))
            throw new ModManagerException("Only Nexus website URLs and nxm manager links are supported.");
        if (!nxm && parts.FirstOrDefault() == "games") parts = parts[1..];
        var game = nxm ? uri.Host : parts[0];
        if (!nxm) parts = parts[1..];
        if (game != "cyberpunk2077") throw new ModManagerException("This build installs Cyberpunk 2077 mods only.");
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var piece in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = piece.Split('=', 2);
            if (!query.TryAdd(Uri.UnescapeDataString(pair[0]), pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : ""))
                throw new ModManagerException("Duplicate URL parameters.");
        }
        static long Positive(string text) => long.TryParse(text, out var value) && value > 0 ? value :
            throw new ModManagerException("Invalid Nexus ID.");
        if (parts.Length == 2 && parts[0] == "mods" && !nxm)
            return new NexusModLink(game, Positive(parts[1]), query.TryGetValue("file_id", out var file) ? Positive(file) : null);
        if (nxm && parts.Length == 4 && parts[0] == "mods" && parts[2] == "files")
        {
            var key = query.GetValueOrDefault("key");
            var expires = query.TryGetValue("expires", out var expiry) ? Positive(expiry) : (long?)null;
            if ((key is not null) != expires.HasValue) throw new ModManagerException("Download authorization needs both key and expiry.");
            return new NexusModLink(game, Positive(parts[1]), Positive(parts[3]), key, expires,
                query.TryGetValue("user_id", out var user) ? Positive(user) : null);
        }
        if (parts.Length is 2 or 4 && parts[0] == "collections")
        {
            if (parts[1].Length is < 1 or > 80 || parts[1].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
                throw new ModManagerException("Invalid collection slug.");
            if (parts.Length == 4 && parts[2] != "revisions") throw new ModManagerException("Invalid collection revision URL.");
            var revision = parts.Length == 4 ? parts[3] : query.GetValueOrDefault("revision", "latest");
            return new NexusCollectionLink(game, parts[1], revision == "latest" ? null : checked((int)Positive(revision)));
        }
        throw new ModManagerException("Use a mod file URL or a collection URL.");
    }
}

public sealed class NexusApi(HttpClient http, string apiKey)
{
    public static readonly Uri BaseUri = new("https://api.nexusmods.com/");

    public async Task<JsonElement> RequestAsync(HttpMethod method, string path, JsonElement? body = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ModManagerException("Connect your Nexus account first.");
        if (!Uri.TryCreate(BaseUri, path, out var uri) || uri.Scheme != "https" || uri.Host != BaseUri.Host ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0) throw new ModManagerException("Unexpected Nexus API endpoint.");
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("apikey", apiKey);
        request.Headers.Add("Application-Name", "Vortnux");
        request.Headers.Add("Application-Version", "0.1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body.HasValue) request.Content = new StringContent(body.Value.GetRawText(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ModManagerException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Nexus rejected the API key.",
                HttpStatusCode.Forbidden => "Nexus denied this operation. Check account/download permissions.",
                HttpStatusCode.NotFound => "This Nexus file or collection revision was not found.",
                HttpStatusCode.TooManyRequests => "Nexus rate limit reached. Wait and retry.",
                _ => $"Nexus returned HTTP {(int)response.StatusCode}. Retry later."
            });
        if (response.Content.Headers.ContentLength > 32 * 1024 * 1024) throw new ModManagerException("API response exceeds the size limit.");
        using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > 32 * 1024 * 1024) throw new ModManagerException("API response exceeds the size limit.");
            buffer.Write(chunk, 0, read);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length == 0 && response.StatusCode == HttpStatusCode.NoContent)
        {
            using var empty = JsonDocument.Parse("null"); return empty.RootElement.Clone();
        }
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() != 0)
            throw new ModManagerException("Nexus could not complete this query. Check account access and retry; its API schema may have changed.");
        return document.RootElement.Clone();
    }

    public async Task<NexusAccount> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(HttpMethod.Get, "v1/users/validate.json", cancellationToken: cancellationToken);
        return new NexusAccount(response.GetProperty("user_id").GetInt64(), response.GetProperty("name").GetString()!, response.GetProperty("is_premium").GetBoolean());
    }

    public async Task<List<CollectionFile>> FilesAsync(NexusModLink link, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(HttpMethod.Get, $"v1/games/{link.Game}/mods/{link.ModId}/files.json", cancellationToken: cancellationToken);
        return response.GetProperty("files").EnumerateArray().Where(file => !file.TryGetProperty("category_id", out var category) || category.GetInt32() != 6)
            .Select(file => new CollectionFile(file.GetProperty("name").GetString()!, link.ModId, file.GetProperty("file_id").GetInt64(), false)).ToList();
    }

    public async Task<Uri> DownloadAsync(NexusModLink link, NexusAccount account, CancellationToken cancellationToken = default)
    {
        if (link.FileId is null) throw new ModManagerException("Choose an exact mod file before downloading.");
        if (link.UserId.HasValue && link.UserId != account.Id) throw new ModManagerException("This download authorization belongs to another account.");
        if (!account.Premium && link.Key is null) throw new ModManagerException("Open this file on Nexus and choose Download with manager, then paste the authorized nxm link.");
        if (link.Expires.HasValue && link.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new ModManagerException("Download authorization expired. Obtain a new manager link.");
        var query = link.Key is null ? "" : $"?key={Uri.EscapeDataString(link.Key)}&expires={link.Expires}";
        var response = await RequestAsync(HttpMethod.Get,
            $"v1/games/{link.Game}/mods/{link.ModId}/files/{link.FileId}/download_link.json{query}", cancellationToken: cancellationToken);
        return ValidateDownloadUri(response[0].GetProperty("URI").GetString()!);
    }

    public async Task<NexusCollection> CollectionAsync(NexusCollectionLink link, CancellationToken cancellationToken = default)
    {
        const string query = "query VortnuxCollection($slug:String,$revision:Int,$domain:String){collectionRevision(slug:$slug,revision:$revision,domainName:$domain){revisionNumber revisionStatus modCount installationInfo externalResources{id} downloadLink collection{name slug} modFiles{fileId optional file{modId name game{domainName}}}}}";
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("query", query); writer.WriteStartObject("variables");
            writer.WriteString("slug", link.Slug); writer.WriteString("domain", link.Game);
            if (link.Revision.HasValue) writer.WriteNumber("revision", link.Revision.Value); else writer.WriteNull("revision");
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        using var body = JsonDocument.Parse(buffer.ToArray());
        var response = await RequestAsync(HttpMethod.Post, "v2/graphql", body.RootElement, cancellationToken);
        var revision = response.GetProperty("data").GetProperty("collectionRevision");
        if (revision.ValueKind != JsonValueKind.Object) throw new ModManagerException("No published revision was returned.");
        if (revision.GetProperty("revisionStatus").GetString() != "published") throw new ModManagerException("Only published collection revisions can be installed.");
        if (revision.GetProperty("externalResources").GetArrayLength() > 0 ||
            !string.IsNullOrWhiteSpace(revision.GetProperty("installationInfo").GetString()))
            throw new ModManagerException("This collection includes external resources or extra installation instructions which this build cannot reproduce.");
        var files = revision.GetProperty("modFiles").EnumerateArray().Select(item =>
        {
            var file = item.GetProperty("file");
            if (file.GetProperty("game").GetProperty("domainName").GetString() != link.Game)
                throw new ModManagerException("Collection contains files for a different game.");
            return new CollectionFile(file.GetProperty("name").GetString()!, ReadId(file.GetProperty("modId")),
                item.GetProperty("fileId").GetInt64(), item.GetProperty("optional").GetBoolean());
        }).ToList();
        if (files.Count == 0 || files.Count != revision.GetProperty("modCount").GetInt32())
            throw new ModManagerException("Collection file inventory is incomplete. No game files were changed.");
        return new NexusCollection(revision.GetProperty("collection").GetProperty("name").GetString()!, link.Slug,
            revision.GetProperty("revisionNumber").GetInt32(), revision.GetProperty("downloadLink").GetString()!, files);
    }

    private static long ReadId(JsonElement value)
    {
        var id = value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : value.GetInt64();
        return id > 0 ? id : throw new ModManagerException("Invalid Nexus file identity.");
    }

    public async Task<Uri> CollectionDownloadAsync(NexusCollection collection, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(collection.DownloadLink) || collection.DownloadLink.Length > 16384 ||
            collection.DownloadLink.Any(char.IsControl) || collection.DownloadLink.Contains('\\') ||
            !Uri.TryCreate(BaseUri, collection.DownloadLink, out var uri) || uri.Scheme != "https" ||
            uri.Host != BaseUri.Host || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ModManagerException("Unexpected collection download resolver.");
        var parts = uri.AbsolutePath.Split('/');
        static bool PositiveId(string value) => long.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0;
        if (parts.Length != 7 || parts[1] is not ("v1" or "v2") || parts[2] != "collections" ||
            parts[3].Length is < 1 or > 80 || parts[3].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) ||
            (parts[1] == "v2" && !PositiveId(parts[3])) || parts[4] != "revisions" || !PositiveId(parts[5]) ||
            parts[6] is not ("download_link" or "download_link.json"))
            throw new ModManagerException("Unexpected collection download resolver.");
        var response = await RequestAsync(HttpMethod.Get, uri.AbsoluteUri, cancellationToken: cancellationToken);
        if (response.ValueKind != JsonValueKind.Object ||
            (!response.TryGetProperty("download_links", out var links) && !response.TryGetProperty("download_link", out links)))
            throw new ModManagerException("Nexus did not return a collection archive download URL. Retry or open the collection on Nexus.");
        if (links.ValueKind == JsonValueKind.Array && links.GetArrayLength() > 0) links = links[0];
        if (links.ValueKind != JsonValueKind.Object || !links.TryGetProperty("URI", out var address) || address.ValueKind != JsonValueKind.String)
            throw new ModManagerException("Nexus did not return a collection archive download URL. Retry or open the collection on Nexus.");
        return ValidateDownloadUri(address.GetString()!);
    }

    public static Uri ValidateDownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
            !(uri.Host == "nexusmods.com" || uri.Host.EndsWith(".nexusmods.com", StringComparison.Ordinal)))
            throw new ModManagerException("Unexpected Nexus download host. Open the file on Nexus instead.");
        return uri;
    }
}

public static class Downloads
{
    public static async Task FetchAsync(HttpClient http, Uri uri, string destination, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        NexusApi.ValidateDownloadUri(uri.AbsoluteUri);
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var temporary = destination + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > 8L * 1024 * 1024 * 1024) throw new ModManagerException("Download exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(response.Content.Headers.ContentLength is > 0 ? 100.0 * total / response.Content.Headers.ContentLength.Value : 0);
                }
                if (response.Content.Headers.ContentLength is { } expected && expected != total) throw new ModManagerException("Download is truncated.");
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
