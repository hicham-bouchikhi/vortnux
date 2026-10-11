using System.Text.Json;
using Vortnux.Core;

namespace Vortnux.Desktop;

public sealed class ReleaseClient : IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    public ReleaseTrust? Trust { get; }
    public ReleaseClient()
    {
        using var stream = typeof(ReleaseClient).Assembly.GetManifestResourceStream("Vortnux.ReleaseTrust");
        if (stream is null) return;
        var trust = JsonSerializer.Deserialize(stream, AppJson.Default.ReleaseTrust);
        if (trust is null || trust.Repository.Split('/') is not [var owner, var repo] ||
            new[] { owner, repo }.Any(part => part.Length == 0 || part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.')) ||
            string.IsNullOrWhiteSpace(trust.PublicKey)) return;
        Trust = trust;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vortnux/0.1.0");
    }

    public async Task<UpdateManifest?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var trust = Trust ?? throw new ModManagerException("This build has no trusted release publisher configured.");
        var bytes = await FetchAsync(new Uri($"https://api.github.com/repos/{trust.Repository}/releases/latest"), 1024 * 1024, cancellationToken);
        using var release = JsonDocument.Parse(bytes);
        var tag = release.RootElement.GetProperty("tag_name").GetString()!;
        if (!tag.StartsWith('v') || !Version.TryParse(tag[1..], out var latest)) throw new ModManagerException("Invalid release version.");
        if (latest <= (typeof(ReleaseClient).Assembly.GetName().Version ?? new Version(0, 1, 0))) return null;
        var prefix = $"https://github.com/{trust.Repository}/releases/download/{Uri.EscapeDataString(tag)}/";
        var manifest = await FetchAsync(new Uri(prefix + "update-linux-x64.json"), 65536, cancellationToken);
        var signature = await FetchAsync(new Uri(prefix + "update-linux-x64.sig"), 8192, cancellationToken);
        var verified = Updates.Verify(manifest, signature, trust.PublicKey);
        if (verified.Version != tag[1..] || !verified.Url.StartsWith(prefix, StringComparison.Ordinal))
            throw new ModManagerException("Signed artifact does not match this publisher and release.");
        return verified;
    }

    public async Task DownloadAsync(UpdateManifest manifest, string path, CancellationToken cancellationToken = default)
    {
        var temporary = path + ".part";
        try
        {
            using var response = await OpenAsync(new Uri(manifest.Url), cancellationToken);
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > manifest.Size) throw new ModManagerException("Release is larger than the signed manifest.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken); output.Flush(true);
            }
            Updates.VerifyArtifact(temporary, manifest); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<byte[]> FetchAsync(Uri uri, int limit, CancellationToken cancellationToken)
    {
        using var response = await OpenAsync(uri, cancellationToken);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream(); var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (result.Length + read > limit) throw new ModManagerException("Release metadata exceeds its size limit.");
            result.Write(chunk, 0, read);
        }
        return result.ToArray();
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new ModManagerException("Unexpected release download host.");
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            { uri = new Uri(uri, next); response.Dispose(); continue; }
            if (!response.IsSuccessStatusCode)
            { var code = (int)response.StatusCode; response.Dispose(); throw new ModManagerException($"Release server returned HTTP {code}."); }
            return response;
        }
        throw new ModManagerException("Too many release redirects.");
    }
    public void Dispose() => http.Dispose();
}
