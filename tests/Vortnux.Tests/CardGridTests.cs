using System.Net;
using System.Net.Http.Headers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vortnux.Core;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class CardGridTests
{
    private static void Click(MainWindow window, string label) => window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, label))
        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static WrapPanel Panel(MainWindow window, string name) => window.GetVisualDescendants().OfType<WrapPanel>().Single(panel => panel.Name == name);
    private static string Catalog(bool collections)
    {
        var names = collections ? new[] { "Night City Essentials", "Street Samurai", "Neon Nights", "Vanilla Enhanced", "City of Dreams", "Afterlife" }
            : new[] { "Cyber Engine Tweaks", "ArchiveXL", "RED4ext", "redscript", "TweakXL", "Codeware" };
        var nodes = names.Select((name, index) => $$$"""
            {"name":"{{{name}}}","summary":"{{{(collections ? "A curated Cyberpunk collection with a reviewed installation manifest." : "Expand your Cyberpunk experience with scripts and modding tools.")}}}","author":"Example author","user":{"name":"Example curator"},"modId":{{{index + 1}}},"slug":"collection-{{{index}}}","downloads":123456,"endorsements":1234,"game":{"domainName":"cyberpunk2077"}}
            """);
        return "{\"data\":{\"" + (collections ? "collectionsV2" : "mods") + "\":{\"nodes\":[" + string.Join(',', nodes) + "],\"totalCount\":6}}}";
    }

    [AvaloniaFact]
    public async Task CatalogCardsWrapIntoRowsAndColumnsAndReflowOnResize()
    {
        using var fixture = new Fixture();
        var preview = Environment.GetEnvironmentVariable("VORTNUX_CATALOG_PREVIEW");
        var publicCatalog = preview is null ? null : await File.ReadAllTextAsync(preview, TestContext.Current.CancellationToken);
        using var http = new HttpClient(new CatalogTests.Handler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "staticdelivery.nexusmods.com")
            {
                var path = Path.Combine(Path.GetDirectoryName(preview!)!, "vortnux-preview-images", Path.GetFileName(request.RequestUri.AbsolutePath));
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(await File.ReadAllBytesAsync(path, token)) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp"); return response;
            }
            if (request.RequestUri.AbsolutePath.EndsWith("validate.json", StringComparison.Ordinal))
                return CatalogTests.Json("{\"user_id\":1,\"name\":\"Demo account\",\"is_premium\":true}");
            var body = await request.Content!.ReadAsStringAsync(token);
            return CatalogTests.Json(body.Contains("collectionsV2", StringComparison.Ordinal) ? Catalog(true) : publicCatalog ?? Catalog(false));
        }));
        var window = new MainWindow(fixture.Manager, httpClient: http) { Width = 1440, Height = 1440 }; window.Show();
        try
        {
            window.Navigate("Settings"); window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "NexusApiKey").Text = "fixture-key";
            Click(window, "Connect");
            if (preview is not null)
            {
                window.GetVisualDescendants().OfType<ComboBox>().Single(box => box.Name == "NexusSort").SelectedIndex = 2;
                for (var attempt = 0; attempt < 500 && window.GetVisualDescendants().OfType<Image>().Count(image => image.Source is not null) < 6; attempt++)
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                Assert.Equal(6, window.GetVisualDescendants().OfType<Image>().Count(image => image.Source is not null));
            }
            CheckWide(window, "NexusCardGrid"); Capture(window, "card-grid-mods.png");
            Click(window, "Browse collections"); CheckWide(window, "NexusCardGrid"); Capture(window, "card-grid-collections.png");
            window.Width = 840; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var cards = Panel(window, "NexusCardGrid").Children;
            Assert.True(cards[1].Bounds.Y > cards[0].Bounds.Y);
            Assert.Equal(cards[0].Bounds.X, cards[1].Bounds.X);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InstalledModsAndCollectionsUseWrappingCards()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 6; index++) fixture.Manager.Install(fixture.Manager.Stager.Stage(fixture.Zip(("mod-" + index + ".archive", "mod-" + index)), fixture.Game.Id, "Mod " + index, "collection-" + index));
        var window = new MainWindow(fixture.Manager) { Width = 1440, Height = 1000 }; window.Show();
        try
        {
            window.Navigate("Mods"); CheckWide(window, "InstalledModGrid");
            window.Navigate("Collections"); CheckWide(window, "InstalledCollectionGrid");
        }
        finally { window.Close(); }
    }

    private static void CheckWide(MainWindow window, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var cards = Panel(window, name).Children; Assert.Equal(6, cards.Count);
        Assert.True(cards[1].Bounds.X > cards[0].Bounds.X);
        Assert.Equal(cards[0].Bounds.Y, cards[1].Bounds.Y);
        Assert.True(cards[3].Bounds.Y > cards[0].Bounds.Y);
    }

    private static void Capture(MainWindow window, string file)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots", file));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        window.CaptureRenderedFrame()?.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}
