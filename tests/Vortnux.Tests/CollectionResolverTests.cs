using Vortnux.Core;

namespace Vortnux.Tests;

public sealed class CollectionResolverTests
{
    private static NexusCollection Collection(string resolver) => new("Fixture", "iszwwe", 481, resolver, []);

    [Theory]
    [InlineData("/v2/collections/11525/revisions/607172/download_link", "/v2/collections/11525/revisions/607172/download_link")]
    [InlineData("https://api.nexusmods.com/v2/collections/11525/revisions/607172/download_link", "/v2/collections/11525/revisions/607172/download_link")]
    [InlineData("v2/collections/11525/revisions/607172/download_link.json", "/v2/collections/11525/revisions/607172/download_link.json")]
    [InlineData("/v1/collections/iszwwe/revisions/481/download_link.json", "/v1/collections/iszwwe/revisions/481/download_link.json")]
    public async Task ResolvesCurrentNumericIdsAndLegacyUrlsWithoutRebuildingTheRevision(string resolver, string expected)
    {
        using var http = new HttpClient(new CatalogTests.Handler((request, _) =>
        {
            Assert.Equal("api.nexusmods.com", request.RequestUri!.Host);
            Assert.Equal(expected, request.RequestUri.AbsolutePath);
            Assert.Equal("fixture-key", request.Headers.GetValues("apikey").Single());
            return Task.FromResult(CatalogTests.Json("{\"download_links\":[{\"URI\":\"https://files.nexusmods.com/collection.7z\"}]}"));
        }));
        var uri = await new NexusApi(http, "fixture-key").CollectionDownloadAsync(Collection(resolver), TestContext.Current.CancellationToken);
        Assert.Equal("https://files.nexusmods.com/collection.7z", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://evil.test/v2/collections/11525/revisions/607172/download_link")]
    [InlineData("http://api.nexusmods.com/v2/collections/11525/revisions/607172/download_link")]
    [InlineData("https://user@api.nexusmods.com/v2/collections/11525/revisions/607172/download_link")]
    [InlineData("https://api.nexusmods.com:8443/v2/collections/11525/revisions/607172/download_link")]
    [InlineData("/v2/users/validate.json")]
    [InlineData("/v2/collections/11525/revisions/607172/delete")]
    [InlineData("/v2/collections/11525/revisions/0/download_link")]
    [InlineData("/v2/collections/slug/revisions/607172/download_link")]
    [InlineData("/v2/collections/11525/revisions/607172/download_link#fragment")]
    [InlineData("/v2/collections/11525/revisions/607172/download_link\n")]
    public async Task InvalidResolverStopsBeforeSendingCredentials(string resolver)
    {
        var requests = 0;
        using var http = new HttpClient(new CatalogTests.Handler((_, _) =>
        {
            requests++; throw new InvalidOperationException("Unexpected HTTP request.");
        }));
        await Assert.ThrowsAsync<ModManagerException>(() => new NexusApi(http, "fixture-key").CollectionDownloadAsync(Collection(resolver), TestContext.Current.CancellationToken));
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("{\"download_link\":{\"URI\":\"https://files.nexusmods.com/collection.zip\"}}")]
    [InlineData("{\"download_links\":[{\"URI\":\"https://files.nexusmods.com/collection.zip\"}]}")]
    public async Task SupportsBothOfficialResponseShapes(string response)
    {
        using var http = new HttpClient(new CatalogTests.Handler((_, _) => Task.FromResult(CatalogTests.Json(response))));
        var uri = await new NexusApi(http, "fixture-key").CollectionDownloadAsync(Collection("/v2/collections/11525/revisions/607172/download_link"), TestContext.Current.CancellationToken);
        Assert.Equal("https://files.nexusmods.com/collection.zip", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"download_links\":[]}")]
    [InlineData("{\"download_link\":null}")]
    [InlineData("{\"download_link\":{\"URI\":42}}")]
    [InlineData("{\"download_link\":{\"URI\":\"https://evil.test/collection.zip\"}}")]
    public async Task EmptyMalformedAndUntrustedDownloadResponsesFailClearly(string response)
    {
        using var http = new HttpClient(new CatalogTests.Handler((_, _) => Task.FromResult(CatalogTests.Json(response))));
        await Assert.ThrowsAsync<ModManagerException>(() => new NexusApi(http, "fixture-key").CollectionDownloadAsync(Collection("/v2/collections/11525/revisions/607172/download_link"), TestContext.Current.CancellationToken));
    }
}
