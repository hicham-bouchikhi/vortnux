using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vortnux.Core;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class CredentialTests
{
    private sealed class Keyring(bool available) : ISecretTool
    {
        public string? Key { get; private set; }
        public bool Available { get; set; } = available;
        public Task<string?> RunAsync(string operation, string profile, string? secret, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.DoesNotContain("fixture-key", profile, StringComparison.Ordinal);
            if (!Available) return Task.FromResult<string?>(null);
            if (operation == "store") Key = secret;
            if (operation == "clear") Key = null;
            return Task.FromResult(operation == "lookup" ? Key : "");
        }
    }

    [Fact]
    public async Task KeyringSaveRestoresWithoutWritingSecretToDisk()
    {
        using var fixture = new Fixture(); var keyring = new Keyring(true);
        var store = new NexusCredentialStore(fixture.Root, keyring);
        await store.SaveAsync("fixture-key", TestContext.Current.CancellationToken);
        Assert.Equal("fixture-key", await new NexusCredentialStore(fixture.Root, keyring).LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "credentials", "nexus-api-key")));
        foreach (var file in Directory.GetFiles(Path.Combine(fixture.Root, "credentials")))
            Assert.DoesNotContain("fixture-key", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await store.ForgetAsync(TestContext.Current.CancellationToken);
        Assert.Null(keyring.Key); Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UnavailableKeyringNeverCreatesLocalKeyFile()
    {
        using var fixture = new Fixture(); var store = new NexusCredentialStore(fixture.Root, new Keyring(false));
        await Assert.ThrowsAsync<ModManagerException>(() => store.SaveAsync("fixture-key", TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "credentials")));
    }

    private static async Task<string> LegacyAsync(Fixture fixture)
    {
        var directory = Path.Combine(fixture.Root, "credentials"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "nexus-api-key");
        await File.WriteAllTextAsync(path, "fixture-key", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "storage"), "file", TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task MigrationPreservesLegacyFileOnFailureAndDeletesAfterVerifiedSave()
    {
        using var fixture = new Fixture(); var path = await LegacyAsync(fixture);
        var keyring = new Keyring(false); var store = new NexusCredentialStore(fixture.Root, keyring);
        await Assert.ThrowsAsync<ModManagerException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.True(File.Exists(path));
        keyring.Available = true;
        Assert.Equal("fixture-key", await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("fixture-key", keyring.Key); Assert.False(File.Exists(path));
        Assert.Equal("fixture-key", await new NexusCredentialStore(fixture.Root, keyring).LoadAsync(TestContext.Current.CancellationToken));
    }

    private sealed class UnverifiedKeyring : ISecretTool
    {
        public Task<string?> RunAsync(string operation, string profile, string? secret, CancellationToken token)
            => Task.FromResult<string?>(operation == "store" ? "" : "wrong-key");
    }

    [Fact]
    public async Task StoreSuccessWithWrongLookupNeverDeletesLegacyFile()
    {
        using var fixture = new Fixture(); var path = await LegacyAsync(fixture);
        var store = new NexusCredentialStore(fixture.Root, new UnverifiedKeyring());
        await Assert.ThrowsAsync<ModManagerException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("fixture-key", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidValuesAndLegacyRemoval()
    {
        using var fixture = new Fixture(); var store = new NexusCredentialStore(fixture.Root, new Keyring(true));
        foreach (var key in new[] { "", "   ", "line\nsecret", new string('x', 4097) })
            await Assert.ThrowsAsync<ModManagerException>(() => store.SaveAsync(key, TestContext.Current.CancellationToken));
        await LegacyAsync(fixture); await store.ForgetAsync(TestContext.Current.CancellationToken);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "credentials")));
    }

    [Fact]
    public async Task RefusesSymlinkAndOversizedLegacyFiles()
    {
        using var fixture = new Fixture(); var store = new NexusCredentialStore(fixture.Root, new Keyring(true));
        var path = await LegacyAsync(fixture);
        await File.WriteAllTextAsync(path, new string('x', 9000), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ModManagerException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        File.Delete(path); var target = Path.Combine(fixture.Root, "target"); await File.WriteAllTextAsync(target, "unchanged", TestContext.Current.CancellationToken);
        File.CreateSymbolicLink(path, target);
        await Assert.ThrowsAsync<ModManagerException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ModManagerException>(() => store.SaveAsync("fixture-key", TestContext.Current.CancellationToken));
        Assert.Equal("unchanged", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LockedKeyringRetainsSavedKeyForRetry()
    {
        using var fixture = new Fixture(); var keyring = new Keyring(true); var store = new NexusCredentialStore(fixture.Root, keyring);
        await store.SaveAsync("fixture-key", TestContext.Current.CancellationToken); keyring.Available = false;
        await Assert.ThrowsAsync<ModManagerException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ModManagerException>(() => store.SaveAsync("replacement-key", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ModManagerException>(() => store.ForgetAsync(TestContext.Current.CancellationToken));
        keyring.Available = true; Assert.Equal("fixture-key", await store.LoadAsync(TestContext.Current.CancellationToken));
        await store.ForgetAsync(TestContext.Current.CancellationToken); Assert.Null(keyring.Key);
    }

    [Fact]
    public async Task SecretToolPassesKeyOnlyThroughStdinAndHandlesUnavailableCommand()
    {
        using var fixture = new Fixture(); var script = Path.Combine(fixture.Root, "fake-secret-tool");
        await File.WriteAllTextAsync(script, """
            #!/bin/sh
            case "$*" in *fixture-key*) exit 2 ;; esac
            case "$1" in
              store) IFS= read -r key; read_status=$?; test "$read_status" -eq 1 && test "$key" = fixture-key ;;
              lookup) printf 'fixture-key\n' ;;
              clear) exit 0 ;;
              *) exit 2 ;;
            esac
            """, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var tool = new SecretTool(script);
        Assert.Equal("", await tool.RunAsync("store", "test-profile", "fixture-key", TestContext.Current.CancellationToken));
        Assert.Equal("fixture-key", await tool.RunAsync("lookup", "test-profile", null, TestContext.Current.CancellationToken));
        Assert.Equal("", await tool.RunAsync("clear", "test-profile", null, TestContext.Current.CancellationToken));
        Assert.Null(await new SecretTool(Path.Combine(fixture.Root, "absent")).RunAsync("store", "test-profile", "fixture-key", TestContext.Current.CancellationToken));
    }

    private static Button Button(MainWindow window, string label) => window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
    private static void Click(MainWindow window, string label) => Button(window, label).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    private static TextBox KeyBox(MainWindow window) => window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "NexusApiKey");
    private static async Task UntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 200 && !ready(); attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.True(ready());
    }
    private static HttpClient Client(bool valid = true) => new(new CatalogTests.Handler((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("validate.json", StringComparison.Ordinal))
        {
            Assert.Equal("fixture-key", request.Headers.GetValues("apikey").Single());
            return Task.FromResult(valid ? CatalogTests.Json("{\"user_id\":1,\"name\":\"Saved account\",\"is_premium\":true}") : new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        }
        return Task.FromResult(CatalogTests.Json(CatalogTests.ModResponse));
    }));

    [AvaloniaFact]
    public async Task ConnectSavesAndRestartRestoresAndForgetDisconnects()
    {
        using var fixture = new Fixture(); using var http = Client(); var keyring = new Keyring(true);
        var store = new NexusCredentialStore(fixture.Root, keyring);
        var window = new MainWindow(fixture.Manager, httpClient: http, credentialStore: store); window.Show();
        window.Navigate("Settings"); KeyBox(window).Text = "fixture-key"; Click(window, "Connect");
        await UntilAsync(() => window.CurrentPage == "Browse");
        Assert.Equal("Browse", window.CurrentPage); Assert.Equal("fixture-key", await store.LoadAsync(TestContext.Current.CancellationToken)); window.Close();
        keyring.Available = false;
        var restarted = new MainWindow(fixture.Manager, httpClient: http, credentialStore: new NexusCredentialStore(fixture.Root, keyring)); restarted.Show();
        try
        {
            await UntilAsync(() => Button(restarted, "Browse").IsEffectivelyEnabled);
            restarted.Navigate("Settings");
            keyring.Available = true; Click(restarted, "Retry saved connection");
            await UntilAsync(() => restarted.CurrentPage == "Browse");
            Assert.Equal("Browse", restarted.CurrentPage);
            restarted.Navigate("Settings"); Assert.Empty(KeyBox(restarted).Text ?? "");
            Click(restarted, "Disconnect"); Assert.Equal("fixture-key", await store.LoadAsync(TestContext.Current.CancellationToken));
            keyring.Available = true;
            Click(restarted, "Forget saved key");
            await UntilAsync(() => keyring.Key is null && Button(restarted, "Connect").IsEffectivelyEnabled);
            Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
            restarted.Navigate("Browse");
            Assert.Contains(restarted.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("Connect your Nexus account", StringComparison.Ordinal) == true);
        }
        finally { restarted.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedStartupValidationRetainsSavedKeyAndRetryConnectsWithoutTyping()
    {
        using var fixture = new Fixture(); var store = new NexusCredentialStore(fixture.Root, new Keyring(true));
        await store.SaveAsync("fixture-key", TestContext.Current.CancellationToken);
        var valid = false;
        using var http = new HttpClient(new CatalogTests.Handler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("validate.json", StringComparison.Ordinal)
                ? valid ? CatalogTests.Json("{\"user_id\":1,\"name\":\"Saved account\",\"is_premium\":true}") : new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                : CatalogTests.Json(CatalogTests.ModResponse))));
        var window = new MainWindow(fixture.Manager, httpClient: http, credentialStore: store); window.Show();
        try
        {
            await UntilAsync(() => Button(window, "Browse").IsEffectivelyEnabled);
            Assert.Equal("fixture-key", await store.LoadAsync(TestContext.Current.CancellationToken));
            window.Navigate("Settings"); Assert.Empty(KeyBox(window).Text ?? "");
            valid = true; Click(window, "Retry saved connection");
            await UntilAsync(() => window.CurrentPage == "Browse");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RestoringKeepsStartupDownloadLinkAndInvalidKeyIsNeverSaved()
    {
        using var fixture = new Fixture(); var store = new NexusCredentialStore(fixture.Root, new Keyring(true));
        await store.SaveAsync("fixture-key", TestContext.Current.CancellationToken); using var http = Client();
        var window = new MainWindow(fixture.Manager, "nxm://cyberpunk2077/mods/2380/files/2?key=example&expires=2000000000&user_id=1", httpClient: http, credentialStore: store);
        window.Show();
        try
        {
            await UntilAsync(() => Button(window, "Browse").IsEffectivelyEnabled);
            Assert.Equal("Downloads", window.CurrentPage);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("Saved account", StringComparison.Ordinal) == true);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), box => box.Text?.StartsWith("nxm://cyberpunk2077/mods/2380/files/2", StringComparison.Ordinal) == true);
        }
        finally { window.Close(); }
        await store.ForgetAsync(TestContext.Current.CancellationToken); using var rejected = Client(false);
        var invalid = new MainWindow(fixture.Manager, httpClient: rejected, credentialStore: store); invalid.Show();
        try
        {
            invalid.Navigate("Settings"); KeyBox(invalid).Text = "fixture-key"; Click(invalid, "Connect");
            Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken)); Assert.Empty(KeyBox(invalid).Text ?? ""); Assert.Equal("Settings", invalid.CurrentPage);
        }
        finally { invalid.Close(); }
    }
}
