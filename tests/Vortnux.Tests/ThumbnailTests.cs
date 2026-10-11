using System.Net;
using System.Net.Http.Headers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SkiaSharp;
using Vortnux.Core;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class ThumbnailTests
{
    private const string ImageUrl = "https://staticdelivery.nexusmods.com/mods/3333/images/fixture.png";
    private static byte[] FixtureImage()
    {
        using var bitmap = new SKBitmap(200, 132);
        using var canvas = new SKCanvas(bitmap); canvas.Clear(new SKColor(74, 47, 124));
        using var paint = new SKPaint { Color = new SKColor(153, 121, 221), IsAntialias = true };
        canvas.DrawCircle(100, 66, 42, paint);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100); return encoded.ToArray();
    }
    private static HttpResponseMessage ImageResponse(HttpRequestMessage request, byte[] bytes, string type = "image/png")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type); return response;
    }

    [Theory]
    [InlineData("http://media.nexusmods.com/a.png")]
    [InlineData("https://media.nexusmods.com.evil.test/a.png")]
    [InlineData("https://evil.test/a.png")]
    [InlineData("https://user:secret@media.nexusmods.com/a.png")]
    [InlineData("file:///tmp/a.png")]
    [InlineData("https://media.nexusmods.com:444/a.png")]
    public async Task UnsafeImageHostsNeverReceiveRequests(string url)
    {
        using var http = new HttpClient(new CatalogTests.Handler((_, _) => throw new InvalidOperationException("Must not send")));
        using var images = new CatalogImages(http); Assert.Null(await images.FetchAsync(url, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImagesAreCredentialFreeBoundedCachedAndClearable()
    {
        var calls = 0; var bytes = FixtureImage();
        using var http = new HttpClient(new CatalogTests.Handler((request, _) =>
        {
            calls++; Assert.False(request.Headers.Contains("apikey")); Assert.Null(request.Headers.Authorization);
            return Task.FromResult(ImageResponse(request, bytes));
        }));
        using var images = new CatalogImages(http);
        Assert.Equal(bytes, await images.FetchAsync(ImageUrl, TestContext.Current.CancellationToken));
        await images.FetchAsync(ImageUrl, TestContext.Current.CancellationToken); Assert.Equal(1, calls);
        images.Clear(); await images.FetchAsync(ImageUrl, TestContext.Current.CancellationToken); Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("truncated")]
    [InlineData("wrong-type")]
    [InlineData("redirect")]
    [InlineData("missing")]
    public async Task InvalidImageResponsesReturnFallback(string failure)
    {
        using var http = new HttpClient(new CatalogTests.Handler((request, _) =>
        {
            var response = ImageResponse(request, [1, 2, 3]);
            switch (failure)
            {
                case "oversize": response.Content.Headers.ContentLength = CatalogImages.MaximumBytes + 1; break;
                case "truncated": response.Content.Headers.ContentLength = 100; break;
                case "wrong-type": response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html"); break;
                case "redirect": response.StatusCode = HttpStatusCode.Redirect; break;
                case "missing": response.StatusCode = HttpStatusCode.NotFound; break;
            }
            return Task.FromResult(response);
        }));
        using var images = new CatalogImages(http); Assert.Null(await images.FetchAsync(ImageUrl, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClosingCancelsInFlightImageRequests()
    {
        using var http = new HttpClient(new CatalogTests.Handler(async (_, token) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return new HttpResponseMessage(HttpStatusCode.OK); }));
        using var images = new CatalogImages(http); var fetch = images.FetchAsync(ImageUrl, TestContext.Current.CancellationToken);
        images.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Theory]
    [InlineData(CatalogKind.Mods)]
    [InlineData(CatalogKind.Collections)]
    public async Task CatalogReadsImagesWithoutDroppingMissingImages(CatalogKind kind)
    {
        var payload = kind == CatalogKind.Mods ? CatalogTests.ModResponse : CatalogTests.CollectionResponse;
        payload = payload.Replace("\"endorsements\":12", kind == CatalogKind.Mods
            ? "\"endorsements\":12,\"thumbnailUrl\":\"" + ImageUrl + "\""
            : "\"endorsements\":12,\"tileImage\":{\"url\":\"" + ImageUrl + "\"}", StringComparison.Ordinal);
        using var http = new HttpClient(new CatalogTests.Handler((_, _) => Task.FromResult(CatalogTests.Json(payload))));
        var result = await new NexusCatalog(new NexusApi(http, "fixture")).SearchAsync(new(kind), TestContext.Current.CancellationToken);
        Assert.Equal(ImageUrl, Assert.Single(result.Items).ImageUrl);
    }

    [AvaloniaFact]
    public async Task PicturesRenderForModsCollectionsAndSelectedDetails()
    {
        using var fixture = new Fixture(); var bytes = FixtureImage();
        using var http = new HttpClient(new CatalogTests.Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "staticdelivery.nexusmods.com") return ImageResponse(request, bytes);
            if (request.RequestUri.AbsolutePath.EndsWith("validate.json", StringComparison.Ordinal))
                return CatalogTests.Json("{\"user_id\":1,\"name\":\"Fixture\",\"is_premium\":true}");
            if (request.RequestUri.AbsolutePath.EndsWith("files.json", StringComparison.Ordinal)) return CatalogTests.Json("{\"files\":[]}");
            var body = await request.Content!.ReadAsStringAsync(token);
            var collections = body.Contains("collectionsV2", StringComparison.Ordinal);
            return CatalogTests.Json((collections ? CatalogTests.CollectionResponse : CatalogTests.ModResponse).Replace("\"endorsements\":12", collections
                ? "\"endorsements\":12,\"tileImage\":{\"url\":\"" + ImageUrl + "\"}"
                : "\"endorsements\":12,\"thumbnailUrl\":\"" + ImageUrl + "\"", StringComparison.Ordinal));
        }));
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            window.Navigate("Settings"); window.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "NexusApiKey").Text = "fixture";
            Click(window, "Connect"); await WaitForImage(window);
            var screenshot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots/browse-images.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            window.CaptureRenderedFrame()?.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Click(window, "View files"); await WaitForImage(window); Assert.Equal("Downloads", window.CurrentPage);
            Click(window, "Back to results"); Click(window, "Browse collections"); await WaitForImage(window);
        }
        finally { window.Close(); }
    }

    private static void Click(MainWindow window, string label) => window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, label))
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitForImage(MainWindow window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!window.GetVisualDescendants().OfType<Image>().Any(image => image.Name == "CatalogThumbnail" && image.Source is not null))
            await Task.Delay(10, timeout.Token);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
