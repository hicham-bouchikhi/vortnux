using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Vortnux.Core;
using Vortnux.Desktop;

namespace Vortnux.Tests;

public sealed class ReleaseClientTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Release(string version) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"tag_name\":\"v" + version + "\"}")
    };

    [Fact]
    public async Task DevelopmentBuildChecksGitHubWithoutSigningKey()
    {
        var requests = 0;
        using var client = new ReleaseClient(new ReleaseTrust("", ""), new Handler(request =>
        {
            requests++;
            Assert.Equal("https://api.github.com/repos/hicham-bouchikhi/vortnux/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.Contains("Vortnux/", request.Headers.UserAgent.ToString());
            return Release("1.0.0");
        }), new Version(0, 1, 0));
        var result = await client.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, requests);
        Assert.Null(client.Trust);
        Assert.Null(result.Update);
        Assert.Contains("v1.0.0 is available", result.Message);
        Assert.Contains("signing key", result.Message);
    }

    [Fact]
    public async Task NoPublishedReleaseIsReportedWithoutClaimingUpToDate()
    {
        using var client = new ReleaseClient(new ReleaseTrust("owner/repo", ""), new Handler(_ => new(HttpStatusCode.NotFound)));
        var result = await client.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Null(result.Update);
        Assert.Equal("No published stable GitHub release is available for owner/repo.", result.Message);
    }

    [Theory]
    [InlineData("0.1.0", "0.1.0.0")]
    [InlineData("0.1.0.0", "0.1.0")]
    [InlineData("0.1.0", "0.2.0")]
    public async Task EqualAndOlderVersionsDoNotFetchUpdateAssets(string latest, string current)
    {
        var requests = 0;
        using var client = new ReleaseClient(new ReleaseTrust("owner/repo", "fixture-key"), new Handler(_ =>
        {
            requests++;
            return Release(latest);
        }), Version.Parse(current));
        var result = await client.CheckAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, requests);
        Assert.Null(result.Update);
        Assert.Equal("You have the latest release", result.Message);
    }

    [Fact]
    public async Task ServerErrorsAreNotTreatedAsNoRelease()
    {
        using var client = new ReleaseClient(new ReleaseTrust("owner/repo", ""), new Handler(_ => new(HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task MissingSignedAssetsAreNotTreatedAsNoRelease()
    {
        using var client = new ReleaseClient(new ReleaseTrust("owner/repo", "fixture-key"), new Handler(request =>
            request.RequestUri!.Host == "api.github.com" ? Release("1.0.0") : new(HttpStatusCode.NotFound)), new Version(0, 1, 0));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.CheckAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyVerifiedManifestCanEnableInstallation(bool tamper)
    {
        using var key = RSA.Create(2048);
        var manifest = new UpdateManifest("1.0.0", "https://github.com/owner/repo/releases/download/v1.0.0/app.AppImage", new string('a', 64), 123);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, AppJson.Default.UpdateManifest);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (tamper) signature[0] ^= 1;
        using var client = new ReleaseClient(new ReleaseTrust("owner/repo", key.ExportSubjectPublicKeyInfoPem()), new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Release("1.0.0");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? signature : bytes) };
        }), new Version(0, 1, 0));
        if (tamper)
            await Assert.ThrowsAsync<ModManagerException>(() => client.CheckAsync(TestContext.Current.CancellationToken));
        else
        {
            var result = await client.CheckAsync(TestContext.Current.CancellationToken);
            Assert.Equal(manifest, result.Update);
            Assert.Equal("Verified release 1.0.0", result.Message);
        }
    }

    [AvaloniaFact]
    public void SettingsButtonChecksReleasesAndShowsNoReleaseMessage()
    {
        using var fixture = new Fixture();
        var requests = 0;
        using var client = new ReleaseClient(new ReleaseTrust("owner/repo", ""), new Handler(_ =>
        {
            requests++;
            return new(HttpStatusCode.NotFound);
        }));
        var window = new MainWindow(fixture.Manager, releaseClient: client);
        window.Show();
        try
        {
            window.Navigate("Settings");
            var button = window.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Check GitHub release"));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, requests);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "No published stable GitHub release is available for owner/repo.");
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Install update & restart"));
        }
        finally { window.Close(); }
    }
}
