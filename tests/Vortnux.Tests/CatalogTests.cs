using System.Net;
using System.Text;
using System.Text.Json;
using Vortnux.Core;

namespace Vortnux.Tests;

public sealed class CatalogTests
{
    public const string ModResponse = """
        {"data":{"mods":{"nodes":[{"name":"RED4ext","summary":"Script extender","author":"Example author","modId":2380,"downloads":123,"endorsements":12,"game":{"domainName":"cyberpunk2077"}}],"totalCount":50}}}
        """;
    public const string CollectionResponse = """
        {"data":{"collectionsV2":{"nodes":[{"name":"Night City","summary":"A collection","user":{"name":"Example curator"},"slug":"night-city","downloads":123,"endorsements":12,"game":{"domainName":"cyberpunk2077"}}],"totalCount":1}}}
        """;
    public sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    public static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(CatalogKind.Mods)]
    [InlineData(CatalogKind.Collections)]
    public async Task SearchUsesVariablesGameFilterPaginationAndTypedResults(CatalogKind kind)
    {
        const string text = "hello \"quoted\" {query}";
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("https://api.nexusmods.com/v2/graphql", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var query = body.RootElement.GetProperty("query").GetString()!;
            Assert.DoesNotContain(text, query);
            var variables = body.RootElement.GetProperty("variables");
            Assert.Equal(24, variables.GetProperty("offset").GetInt32()); Assert.Equal(24, variables.GetProperty("count").GetInt32());
            var filter = variables.GetProperty("filter");
            Assert.Equal("cyberpunk2077", filter.GetProperty(kind == CatalogKind.Mods ? "gameDomainName" : "gameDomain")[0].GetProperty("value").GetString());
            Assert.Equal(text, filter.GetProperty("name")[0].GetProperty("value").GetString());
            Assert.Equal("WILDCARD", filter.GetProperty("name")[0].GetProperty("op").GetString());
            Assert.Equal("DESC", variables.GetProperty("sort")[0].GetProperty("downloads").GetProperty("direction").GetString());
            if (kind == CatalogKind.Collections) Assert.True(filter.GetProperty("hasPublishedRevision")[0].GetProperty("value").GetBoolean());
            return Json(kind == CatalogKind.Mods ? ModResponse : CollectionResponse);
        }));
        var result = await new NexusCatalog(new NexusApi(http, "fixture")).SearchAsync(new CatalogQuery(kind, text, CatalogSort.Downloads, 24), TestContext.Current.CancellationToken);
        var item = Assert.Single(result.Items); Assert.Equal(kind, item.Kind);
        Assert.IsType<NexusModLink>(NexusLinks.Parse(new CatalogItem(CatalogKind.Mods, "", "", "", 2380, null, 0, 0).Url));
        Assert.Equal(kind == CatalogKind.Mods ? "Example author" : "Example curator", item.Author);
        Assert.Equal(kind == CatalogKind.Mods ? 50 : 1, result.Total);
        Assert.NotNull(NexusLinks.Parse(item.Url));
    }

    [Theory]
    [InlineData("{\"data\":{\"mods\":null}}")]
    [InlineData("{\"data\":{\"mods\":{\"nodes\":[],\"totalCount\":-1}}}")]
    [InlineData("{\"data\":{\"mods\":{\"totalCount\":0}}}")]
    [InlineData("{\"errors\":[{\"message\":\"private details\"}],\"data\":{\"mods\":null}}")]
    public async Task InvalidOrPartialResponsesAreErrorsInsteadOfEmptyCatalogs(string response)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(response))));
        var error = await Assert.ThrowsAsync<ModManagerException>(() => new NexusCatalog(new NexusApi(http, "fixture")).SearchAsync(new(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private details", error.Message);
    }

    [Fact]
    public async Task OtherGameAndUnsafeCollectionSlugAreRejected()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(ModResponse.Replace("cyberpunk2077", "skyrim", StringComparison.Ordinal)))));
        await Assert.ThrowsAsync<ModManagerException>(() => new NexusCatalog(new NexusApi(http, "fixture")).SearchAsync(new(), TestContext.Current.CancellationToken));
        using var otherHttp = new HttpClient(new Handler((_, _) => Task.FromResult(Json(CollectionResponse.Replace("night-city", "../evil?key=token", StringComparison.Ordinal)))));
        await Assert.ThrowsAsync<ModManagerException>(() => new NexusCatalog(new NexusApi(otherHttp, "fixture")).SearchAsync(new(CatalogKind.Collections), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BoundsAreRejectedBeforeNetwork()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Must not send")));
        var catalog = new NexusCatalog(new NexusApi(http, "fixture"));
        foreach (var query in new[] { new CatalogQuery(Text: new string('a', 201)), new CatalogQuery(Offset: -1), new CatalogQuery(Count: 49), new CatalogQuery(Sort: (CatalogSort)99) })
            await Assert.ThrowsAsync<ModManagerException>(() => catalog.SearchAsync(query, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task PermissionAndRateLimitsRemainVisible(HttpStatusCode code)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(code))));
        using var session = new CatalogSession(new NexusCatalog(new NexusApi(http, "fixture")));
        await session.SearchAsync(new());
        Assert.Null(session.Result); Assert.False(session.Loading); Assert.NotNull(session.Error);
        Assert.Contains(code == HttpStatusCode.Forbidden ? "denied" : "rate limit", session.Error);
    }

    private sealed class Catalog(Func<CatalogQuery, CancellationToken, Task<CatalogPage>> search) : INexusCatalog
    { public Task<CatalogPage> SearchAsync(CatalogQuery query, CancellationToken cancellationToken = default) => search(query, cancellationToken); }

    [Fact]
    public async Task SupersededRequestsCannotReplaceNewResultsEvenIfServerIgnoresCancellation()
    {
        var old = new TaskCompletionSource<CatalogPage>(); CancellationToken oldToken = default;
        using var session = new CatalogSession(new Catalog((query, token) =>
        {
            if (query.Text == "old") { oldToken = token; return old.Task; }
            return Task.FromResult(new CatalogPage([], 7));
        }));
        var first = session.SearchAsync(new(Text: "old"));
        await session.SearchAsync(new(Text: "new")); Assert.True(oldToken.IsCancellationRequested);
        old.SetResult(new CatalogPage([], 99)); await first;
        Assert.Equal(7, session.Result!.Total); Assert.Equal("new", session.Query.Text); Assert.False(session.Loading);
    }

    [Fact]
    public async Task CacheIsSessionScopedRefreshableAndClearedOnDisconnect()
    {
        var calls = 0;
        using var session = new CatalogSession(new Catalog((_, _) => Task.FromResult(new CatalogPage([], ++calls))));
        await session.SearchAsync(new()); await session.SearchAsync(new()); Assert.Equal(1, calls);
        await session.SearchAsync(new(), refresh: true); Assert.Equal(2, calls);
        session.Dispose(); Assert.Null(session.Result);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SearchAsync(new()));
    }

    [Fact]
    public async Task DisconnectAndCancellationDiscardPendingResults()
    {
        var response = new TaskCompletionSource<CatalogPage>();
        using var session = new CatalogSession(new Catalog((_, _) => response.Task));
        var request = session.SearchAsync(new()); Assert.True(session.Loading);
        session.Dispose(); response.SetResult(new CatalogPage([], 1)); await request;
        Assert.Null(session.Result); Assert.False(session.Loading);
    }
}
