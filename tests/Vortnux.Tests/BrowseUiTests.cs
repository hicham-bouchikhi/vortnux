using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vortnux.Core;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class BrowseUiTests
{
    private static Button FindButton(MainWindow window, string label) => window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, label));
    private static void Click(MainWindow window, string label) => FindButton(window, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Named<T>(MainWindow window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static bool HasText(MainWindow window, string text) => window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text?.Contains(text, StringComparison.Ordinal) == true);
    private static void Connect(MainWindow window)
    {
        window.Navigate("Settings");
        Named<TextBox>(window, "NexusApiKey").Text = "fixture-key";
        Click(window, "Connect");
    }
    private static HttpClient Client(List<string>? bodies = null) => new(new CatalogTests.Handler(async (request, token) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("validate.json", StringComparison.Ordinal))
            return CatalogTests.Json("{\"user_id\":1,\"name\":\"Test account\",\"is_premium\":true}");
        if (request.RequestUri.AbsolutePath.EndsWith("files.json", StringComparison.Ordinal))
            return CatalogTests.Json("{\"files\":[{\"name\":\"RED4ext archive\",\"file_id\":2}]}");
        var text = await request.Content!.ReadAsStringAsync(token); bodies?.Add(text);
        if (text.Contains("VortnuxCollection", StringComparison.Ordinal)) return CatalogTests.Json("""
            {"data":{"collectionRevision":{"revisionNumber":2,"revisionStatus":"published","modCount":1,"installationInfo":null,"externalResources":[],"downloadLink":"/v1/collections/night-city/revisions/2/download_link.json","collection":{"name":"Night City"},"modFiles":[{"fileId":2,"optional":false,"file":{"modId":"2380","name":"RED4ext","game":{"domainName":"cyberpunk2077"}}}]}}}
            """);
        return CatalogTests.Json(text.Contains("collectionsV2", StringComparison.Ordinal) ? CatalogTests.CollectionResponse : CatalogTests.ModResponse);
    }));

    [AvaloniaFact]
    public void ConnectLoadsCatalogSearchPagingSortingAndCollectionTab()
    {
        using var fixture = new Fixture(); var requests = new List<string>(); using var http = Client(requests);
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            Connect(window); Assert.Equal("Browse", window.CurrentPage); Assert.True(HasText(window, "RED4ext"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var screenshot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/screenshots/browse.png"));
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            window.CaptureRenderedFrame()?.Save(screenshot, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Named<TextBox>(window, "NexusSearch").Text = "RED4ext"; Click(window, "Search");
            Assert.Contains("RED4ext", requests.Last());
            Click(window, "Next page");
            using (var request = System.Text.Json.JsonDocument.Parse(requests.Last()))
                Assert.Equal(24, request.RootElement.GetProperty("variables").GetProperty("offset").GetInt32());
            Named<ComboBox>(window, "NexusSort").SelectedIndex = 2;
            Assert.Contains("\"downloads\":{\"direction\":\"DESC\"}", requests.Last());
            Click(window, "Browse collections"); Assert.True(HasText(window, "Night City"));
            Assert.False(FindButton(window, "Next page").IsEnabled);
            window.Navigate("Settings"); Click(window, "Disconnect"); window.Navigate("Browse");
            Assert.True(HasText(window, "Connect your Nexus account")); Assert.False(HasText(window, "Night City"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ModResultOpensExactFilesAndReturnsToSearchResults()
    {
        using var fixture = new Fixture(); using var http = Client();
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            Connect(window); Click(window, "View files");
            Assert.Equal("Downloads", window.CurrentPage); Assert.True(HasText(window, "RED4ext archive"));
            Assert.True(HasText(window, "Mod 2380 · file 2"));
            Click(window, "Back to results"); Assert.Equal("Browse", window.CurrentPage); Assert.True(HasText(window, "RED4ext"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CollectionResultResolvesItsPublishedRevision()
    {
        using var fixture = new Fixture(); using var http = Client();
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            Connect(window); Click(window, "Browse collections"); Click(window, "View collection");
            Assert.Equal("Downloads", window.CurrentPage); Assert.True(HasText(window, "Revision 2 · 1 files"));
            Assert.NotNull(FindButton(window, "Download & check manifest"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InstalledModFiltersAndNotificationClearsFilters()
    {
        using var fixture = new Fixture(); var mod = fixture.Stage(); var activity = fixture.Manager.Install(mod);
        var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            Named<TextBox>(window, "InstalledModSearch").Text = "missing"; Assert.True(HasText(window, "No installed mods match"));
            Named<TextBox>(window, "InstalledModSearch").Text = "exAMPle"; Assert.True(HasText(window, "Example"));
            Named<ComboBox>(window, "InstalledModState").SelectedIndex = 2; Assert.True(HasText(window, "No installed mods match"));
            window.Activate(activity); Assert.Equal(mod.Id, window.FocusedEntity); Assert.True(HasText(window, "Example"));
            Assert.Equal("", Named<TextBox>(window, "InstalledModSearch").Text); Assert.Equal(0, Named<ComboBox>(window, "InstalledModState").SelectedIndex);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InstalledCollectionsFilterByCollectionAndMemberName()
    {
        using var fixture = new Fixture(); var mod = fixture.Stage(collection: "night-city@1"); fixture.Manager.Install(mod);
        var window = new MainWindow(fixture.Manager); window.Show();
        try
        {
            window.Navigate("Collections"); Named<TextBox>(window, "InstalledCollectionSearch").Text = "missing";
            Assert.True(HasText(window, "No installed collections match"));
            Named<TextBox>(window, "InstalledCollectionSearch").Text = "EXAMPLE"; Assert.True(HasText(window, "night-city@1"));
        }
        finally { window.Close(); }
    }
}
