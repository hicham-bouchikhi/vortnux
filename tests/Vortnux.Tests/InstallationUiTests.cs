using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vortnux.Core;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class InstallationUiTests
{
    private const string Revision = """
        {"data":{"collectionRevision":{"revisionNumber":2,"revisionStatus":"published","modCount":1,"installationInfo":null,"externalResources":[],"downloadLink":"/v2/collections/11525/revisions/607172/download_link","collection":{"name":"Night City"},"modFiles":[{"fileId":2,"optional":false,"file":{"modId":"2380","name":"RED4ext","game":{"domainName":"cyberpunk2077"}}}]}}}
        """;
    private static HttpClient Client(Fixture fixture, bool premium = true, List<string>? requests = null, Func<CancellationToken, Task>? waitForDownload = null)
    {
        var archive = File.ReadAllBytes(fixture.Zip(("example.archive", "downloaded mod")));
        var manifest = ApiTests.Manifest.Replace("\"modId\":1", "\"modId\":2380", StringComparison.Ordinal);
        var bundle = File.ReadAllBytes(fixture.Zip(("collection.json", manifest)));
        return new HttpClient(new CatalogTests.Handler(async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath; requests?.Add(path);
            if (request.RequestUri.Host == "files.nexusmods.com")
            {
                if (waitForDownload is not null) await waitForDownload(token);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(path == "/collection.zip" ? bundle : archive) };
            }
            if (path.EndsWith("validate.json", StringComparison.Ordinal)) return CatalogTests.Json("{\"user_id\":1,\"name\":\"Fixture\",\"is_premium\":" + (premium ? "true" : "false") + "}");
            if (path.EndsWith("files.json", StringComparison.Ordinal)) return CatalogTests.Json("{\"files\":[{\"name\":\"RED4ext archive\",\"file_id\":2}]}");
            if (path.StartsWith("/v2/collections/", StringComparison.Ordinal)) return CatalogTests.Json("{\"download_links\":[{\"URI\":\"https://files.nexusmods.com/collection.zip\"}]}");
            if (path.EndsWith("download_link.json", StringComparison.Ordinal)) return CatalogTests.Json("[{\"URI\":\"https://files.nexusmods.com/mod.zip\"}]");
            var query = await request.Content!.ReadAsStringAsync(token);
            return CatalogTests.Json(query.Contains("VortnuxCollection", StringComparison.Ordinal) ? Revision :
                query.Contains("collectionsV2", StringComparison.Ordinal) ? CatalogTests.CollectionResponse : CatalogTests.ModResponse);
        }));
    }
    private static Button FindButton(MainWindow window, string label) => window.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, label));
    private static void Click(MainWindow window, string label) => FindButton(window, label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Connect(MainWindow window)
    {
        window.Navigate("Settings"); window.GetVisualDescendants().OfType<TextBox>().Single(input => input.Name == "NexusApiKey").Text = "fixture";
        Click(window, "Connect");
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private static bool HasButton(MainWindow window, string label) => window.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, label));

    [AvaloniaTheory]
    [InlineData(CatalogKind.Mods)]
    [InlineData(CatalogKind.Collections)]
    public async Task InstallButtonDownloadsReviewsDeploysAndCreatesExactActivity(CatalogKind kind)
    {
        using var fixture = new Fixture(); var requests = new List<string>(); using var http = Client(fixture, requests: requests);
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            Connect(window); if (kind == CatalogKind.Collections) Click(window, "Browse collections");
            Click(window, "Install");
            var select = kind == CatalogKind.Mods ? "Install this file" : "Install collection";
            await WaitUntil(() => HasButton(window, select)); Assert.Equal("Downloads", window.CurrentPage);
            Click(window, select); await WaitUntil(() => HasButton(window, "Install reviewed files"));
            Assert.Empty(fixture.Manager.State.Mods); Assert.False(File.Exists(fixture.File("archive/pc/mod/example.archive")));
            Assert.Contains("/v1/games/cyberpunk2077/mods/2380/files/2/download_link.json", requests);
            if (kind == CatalogKind.Collections) Assert.Contains("/v2/collections/11525/revisions/607172/download_link", requests);
            Click(window, "Install reviewed files");
            await WaitUntil(() => fixture.Manager.State.Mods.Count == 1 && !HasButton(window, "Install reviewed files"));
            Assert.Equal("downloaded mod", File.ReadAllText(fixture.File("archive/pc/mod/example.archive")));
            var mod = Assert.Single(fixture.Manager.State.Mods);
            Assert.Equal(kind == CatalogKind.Collections ? "night-city@2" : null, mod.CollectionId);
            var activity = fixture.Manager.State.Activities.Last(); window.Activate(activity);
            Assert.Equal(kind == CatalogKind.Mods ? "Mods" : "Collections", window.CurrentPage);
            Assert.Equal(kind == CatalogKind.Mods ? mod.Id : mod.CollectionId, window.FocusedEntity);
            fixture.Manager.Remove(mod.Id); Assert.False(File.Exists(fixture.File("archive/pc/mod/example.archive")));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void InstallWithoutGameOpensSetupBeforeResolvingFiles()
    {
        using var fixture = new Fixture(); var requests = new List<string>(); using var http = Client(fixture, requests: requests);
        var empty = new ModManager(new StateStore(Path.Combine(fixture.Root, "empty")), () => false);
        var window = new MainWindow(empty, httpClient: http); window.Show();
        try
        {
            Connect(window); Click(window, "Install"); Assert.Equal("Games", window.CurrentPage);
            Assert.DoesNotContain(requests, path => path.EndsWith("files.json", StringComparison.Ordinal)); Assert.Empty(empty.State.Mods);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FreeAccountInstallationOffersAuthorizedBrowserDownload()
    {
        using var fixture = new Fixture(); using var http = Client(fixture, premium: false);
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            Connect(window); Click(window, "Install"); Assert.Equal("Downloads", window.CurrentPage);
            Assert.True(HasButton(window, "Open file on Nexus")); Assert.False(HasButton(window, "Install this file"));
            Assert.Empty(fixture.Manager.State.Mods);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAllowsBrowsingQueuesLinksAndRecordsOutcome(bool cancel)
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = Client(fixture, waitForDownload: token => release.Task.WaitAsync(token));
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            Connect(window); Click(window, "Install");
            await WaitUntil(() => HasButton(window, "Install this file"));
            Click(window, "Install this file");
            var operation = Assert.Single(window.Operations.Entries);
            Assert.Equal(OperationStatus.Running, operation.Status);
            Click(window, "Browse"); Assert.Equal("Browse", window.CurrentPage);
            Assert.True(FindButton(window, "Search").IsEffectivelyEnabled);
            window.ReceiveLaunch("nxm://cyberpunk2077/mods/99/files/100?key=private&expires=9999999999");
            Assert.Equal("Browse", window.CurrentPage);
            var queued = Assert.Single(window.Operations.Entries, entry => entry.Status == OperationStatus.Queued);
            Assert.DoesNotContain("private", queued.Message);
            if (cancel) window.Operations.Cancel(operation); else release.SetResult();
            await WaitUntil(() => operation.Status != OperationStatus.Running);
            Assert.Equal(cancel ? OperationStatus.Cancelled : OperationStatus.Completed, operation.Status);
            await WaitUntil(() => window.Operations.ActiveCount == 1);
            Assert.Equal(OperationStatus.Queued, queued.Status);
            window.Navigate("Downloads");
            Assert.Equal(!cancel, HasButton(window, "Install reviewed files"));
            Assert.Empty(fixture.Manager.State.Mods);
            window.Operations.ClearHistory();
            Assert.Same(queued, Assert.Single(window.Operations.Entries));
            window.Operations.Cancel(queued);
            Assert.Equal(OperationStatus.Cancelled, queued.Status);
            Assert.Equal(0, window.Operations.ActiveCount);
        }
        finally { release.TrySetResult(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedDownloadIsRetainedInNotificationHistory()
    {
        using var fixture = new Fixture();
        using var http = Client(fixture, waitForDownload: _ => throw new HttpRequestException("fixture failure"));
        var window = new MainWindow(fixture.Manager, httpClient: http); window.Show();
        try
        {
            Connect(window); Click(window, "Install");
            await WaitUntil(() => HasButton(window, "Install this file"));
            Click(window, "Install this file");
            await WaitUntil(() => window.Operations.Entries.Any(entry => entry.Status == OperationStatus.Failed));
            Assert.Equal(1, window.Operations.UnreadCount);
            Assert.Empty(fixture.Manager.State.Mods);
        }
        finally { window.Close(); }
    }

}
