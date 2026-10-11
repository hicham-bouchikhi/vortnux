using System.Net;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Vortnux.Core;

namespace Vortnux.Tests;

public sealed class ApiTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("https://www.nexusmods.com/cyberpunk2077/mods/1?file_id=2")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2?key=abc&expires=9999999999&user_id=3")]
    public void ModLinksPinFile(string link)
    {
        var parsed = Assert.IsType<NexusModLink>(NexusLinks.Parse(link)); Assert.Equal(1, parsed.ModId); Assert.Equal(2, parsed.FileId);
    }

    [Theory]
    [InlineData("https://next.nexusmods.com/cyberpunk2077/collections/abc/revisions/3")]
    [InlineData("https://www.nexusmods.com/games/cyberpunk2077/collections/abc?revision=3")]
    public void CollectionLinksPinRevision(string link)
    {
        var parsed = Assert.IsType<NexusCollectionLink>(NexusLinks.Parse(link)); Assert.Equal("abc", parsed.Slug); Assert.Equal(3, parsed.Revision);
    }

    [Theory]
    [InlineData("https://evil.test/cyberpunk2077/mods/1")]
    [InlineData("https://www.nexusmods.com/skyrim/mods/1")]
    [InlineData("nxm://cyberpunk2077/mods/1/files/2?key=a")]
    [InlineData("https://www.nexusmods.com/cyberpunk2077/mods/1?file_id=2&file_id=3")]
    public void InvalidLinksRejected(string link) => Assert.Throws<ModManagerException>(() => NexusLinks.Parse(link));

    [Fact]
    public async Task FreeAccountNeedsAuthorizationAndExpiredLinksStopBeforeHttp()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("must not send")));
        var api = new NexusApi(http, "fixture-key"); var user = new NexusAccount(1, "test", false);
        await Assert.ThrowsAsync<ModManagerException>(() => api.DownloadAsync(new NexusModLink("cyberpunk2077", 1, 2), user, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ModManagerException>(() => api.DownloadAsync(new NexusModLink("cyberpunk2077", 1, 2, "key", 1), user, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApiKeyOnlyGoesToApprovedHostAndErrorsAreRedacted()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("api.nexusmods.com", request.RequestUri!.Host);
            Assert.Equal("fixture-key", Assert.Single(request.Headers.GetValues("apikey")));
            return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("secret-token") };
        }));
        var api = new NexusApi(http, "fixture-key");
        await Assert.ThrowsAsync<ModManagerException>(() => api.RequestAsync(HttpMethod.Get, "https://evil.test", cancellationToken: TestContext.Current.CancellationToken));
        var error = await Assert.ThrowsAsync<ModManagerException>(() => api.ValidateAsync(TestContext.Current.CancellationToken)); Assert.DoesNotContain("secret-token", error.Message);
    }

    [Fact]
    public async Task PremiumDownloadResolvesWithoutReflectionSerialization()
    {
        using var http = new HttpClient(new Handler(_ => Json("[{\"URI\":\"https://premium-files.nexusmods.com/file.zip\"}]")));
        var uri = await new NexusApi(http, "fixture").DownloadAsync(new NexusModLink("cyberpunk2077", 1, 2), new NexusAccount(1, "test", true), TestContext.Current.CancellationToken);
        Assert.Equal("premium-files.nexusmods.com", uri.Host);
    }

    [Fact]
    public async Task TruncatedDownloadDeletesPartialFile()
    {
        using var fixture = new Fixture(); var path = Path.Combine(fixture.Root, "download.zip");
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = Json("123"); response.Content.Headers.ContentLength = 100; return response;
        }));
        await Assert.ThrowsAsync<ModManagerException>(() => Downloads.FetchAsync(http, new Uri("https://files.nexusmods.com/file"), path, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".part"));
    }

    public const string Manifest = """
        {"info":{"name":"Fixture","domainName":"cyberpunk2077","installInstructions":""},"mods":[{"name":"Example","domainName":"cyberpunk2077","optional":false,"source":{"type":"nexus","modId":1,"fileId":2,"updatePolicy":"exact"}}],"modRules":[]}
        """;

    [Fact]
    public void CollectionManifestPinsFilesAndRejectsUnreproducibleChoices()
    {
        using var document = JsonDocument.Parse(Manifest); var plan = Collections.Parse(document.RootElement);
        Assert.Equal(2, Assert.Single(plan.Entries).FileId);
        using var unsupported = JsonDocument.Parse(Manifest.Replace("\"optional\":false", "\"optional\":false,\"choices\":{\"x\":true}", StringComparison.Ordinal));
        Assert.Throws<ModManagerException>(() => Collections.Parse(unsupported.RootElement));
        using var external = JsonDocument.Parse(Manifest.Replace("\"nexus\"", "\"direct\"", StringComparison.Ordinal));
        Assert.Throws<ModManagerException>(() => Collections.Parse(external.RootElement));
    }

    [Fact]
    public void ReleaseSignatureAndArtifactMustBothMatch()
    {
        using var fixture = new Fixture(); using var key = RSA.Create(2048);
        var path = Path.Combine(fixture.Root, "release"); File.WriteAllText(path, "binary");
        var manifest = new UpdateManifest("1.0.0", "https://github.com/owner/repo/releases/download/v1/app.AppImage", SafeFiles.Hash(path), new FileInfo(path).Length);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, AppJson.Default.UpdateManifest);
        var signature = key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var verified = Updates.Verify(bytes, signature, key.ExportSubjectPublicKeyInfoPem()); Updates.VerifyArtifact(path, verified);
        var altered = bytes.ToArray(); altered[0] ^= 1;
        Assert.Throws<ModManagerException>(() => Updates.Verify(altered, signature, key.ExportSubjectPublicKeyInfoPem()));
        File.WriteAllText(path, "corrupted"); Assert.Throws<ModManagerException>(() => Updates.VerifyArtifact(path, verified));
    }

    [Fact]
    public async Task GeneratedV3ClientUsesEncodedRoutesAndAotMetadata()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/v3/games/cyberpunk2077/trending-mods", request.RequestUri!.AbsolutePath);
            return Json("{\"data\":{\"mods\":[]}}");
        }));
        var client = new Vortnux.Core.Generated.NexusV3Client(new NexusApi(http, "fixture"));
        var response = await client.GetTrendingModsAsync("cyberpunk2077", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(response.Data);
        Assert.Empty(response.Data.Mods!);
    }

    [Fact]
    public void EveryGeneratedDtoHasUsableSourceGeneratedMetadata()
    {
        foreach (var fixture in Vortnux.Core.Generated.NexusV3Contracts.Fixtures)
            Assert.NotNull(JsonSerializer.Deserialize(fixture.Json, fixture.Type, Vortnux.Core.Generated.NexusV3Json.Default));
    }

    [Fact]
    public async Task CollectionQueryReadsPublishedRevisionAndStringModIds()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method); Assert.Equal("/v2/graphql", request.RequestUri!.AbsolutePath);
            return Json("""
                {"data":{"collectionRevision":{"revisionNumber":2,"revisionStatus":"published","modCount":1,"installationInfo":null,"externalResources":[],"downloadLink":"/v1/collections/abc/revisions/2/download_link.json","collection":{"name":"Fixture"},"modFiles":[{"fileId":2,"optional":false,"file":{"modId":"1","name":"Example","game":{"domainName":"cyberpunk2077"}}}]}}}
                """);
        }));
        var collection = await new NexusApi(http, "fixture").CollectionAsync(new NexusCollectionLink("cyberpunk2077", "abc", 2), TestContext.Current.CancellationToken);
        Assert.Equal(2, collection.Revision); Assert.Equal(1, Assert.Single(collection.Files).ModId);
    }
}
