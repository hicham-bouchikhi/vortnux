namespace Vortnux.Core;

// No credentials or redirects: catalog image URLs are data from the remote API.
public sealed class CatalogImages(HttpClient http) : IDisposable
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private const int CacheBytes = 32 * 1024 * 1024;
    private readonly SemaphoreSlim slots = new(4);
    private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset Access)> cache = [];
    private readonly CancellationTokenSource lifetime = new();
    private int cachedBytes;
    private bool disposed;

    public static bool IsAllowed(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host is "staticdelivery.nexusmods.com" or "media.nexusmods.com";

    public async Task<byte[]?> FetchAsync(string? url, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsAllowed(url)) return null;
        lock (cache)
        {
            if (cache.TryGetValue(url!, out var saved))
            { cache[url!] = (saved.Bytes, DateTimeOffset.UtcNow); return saved.Bytes; }
        }
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        requestLifetime.CancelAfter(TimeSpan.FromSeconds(15));
        var token = requestLifetime.Token;
        await slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("image/png,image/jpeg,image/webp");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.RequestMessage?.RequestUri?.AbsoluteUri != new Uri(url!).AbsoluteUri ||
                response.Content.Headers.ContentLength > MaximumBytes) return null;
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/png" or "image/jpeg" or "image/webp")) return null;
            using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[32768]; int read;
            while ((read = await input.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaximumBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            if (buffer.Length == 0 || response.Content.Headers.ContentLength is { } expected && expected != buffer.Length) return null;
            var bytes = buffer.ToArray();
            lock (cache)
            {
                if (disposed) return null;
                if (cache.TryGetValue(url!, out var previous)) { cachedBytes -= previous.Bytes.Length; cache.Remove(url!); }
                while (cache.Count >= 64 || cachedBytes + bytes.Length > CacheBytes)
                {
                    var oldest = cache.MinBy(item => item.Value.Access); cache.Remove(oldest.Key); cachedBytes -= oldest.Value.Bytes.Length;
                }
                cache[url!] = (bytes, DateTimeOffset.UtcNow); cachedBytes += bytes.Length;
            }
            return bytes;
        }
        finally { slots.Release(); }
    }

    public void Clear()
    { lock (cache) { cache.Clear(); cachedBytes = 0; } }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); Clear(); lifetime.Dispose();
        // In-flight requests still release the semaphore when cancellation is observed.
    }
}
