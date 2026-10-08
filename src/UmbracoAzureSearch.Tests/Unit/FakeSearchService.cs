using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Sync;
using UmbracoAzureSearch.Extensions;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Tests.Unit;

// Answers Azure AI Search calls with an empty index or result and records the requests.
internal sealed class FakeSearchService : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    /// <summary>A field the fake index does not have: a search naming it is answered as Azure does, with a 400.</summary>
    public string? UnknownField { get; set; }

    /// <summary>Report <see cref="UnknownField"/> for every search, whether the search names it or not.</summary>
    public bool UnknownFieldAlways { get; set; }

    public IServiceProvider CreateServices(ServerRole serverRole = ServerRole.Single)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UmbracoAzureSearch:Endpoint"] = "https://unit-test.search.windows.net",
                ["UmbracoAzureSearch:Key"] = "test-key",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IServerRoleAccessor>(new FixedServerRoleAccessor(serverRole))
            .AddUmbracoAzureSearch(configuration)
            .BuildServiceProvider();
        ((AzureSearchClientFactory)services.GetRequiredService<IAzureSearchClientFactory>()).Transport =
            new HttpClientTransport(new HttpClient(this));
        return services;
    }

    public IAzureSearchSearcher CreateSearcher(ServerRole serverRole = ServerRole.Single)
        => CreateServices(serverRole).GetRequiredService<IAzureSearchSearcher>();

    public JsonElement LastSearchBody()
    {
        var body = Requests.Last(r => r.Path.Contains("search.post.search")).Body;
        Assert.That(body, Is.Not.Null);
        return JsonDocument.Parse(body!).RootElement;
    }

    public string? LastFilter()
        => LastSearchBody().TryGetProperty("filter", out var filter) ? filter.GetString() : null;

    public string? LastOrderBy()
        => LastSearchBody().TryGetProperty("orderby", out var orderBy) ? orderBy.GetString() : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));

        if (request.Method == HttpMethod.Delete)
            return new HttpResponseMessage(HttpStatusCode.NoContent);

        if (UnknownField is { } unknown && request.RequestUri!.AbsolutePath.Contains("search.post.search")
            && (UnknownFieldAlways || body?.Contains(unknown) == true))
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    $$$"""{"error":{"code":"","message":"Invalid expression: Could not find a property named '{{{unknown}}}' on type 'search.document'.\r\nParameter name: $orderby"}}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        var json = request.RequestUri.AbsolutePath.Contains("/docs/")
            ? """{"@odata.count":0,"value":[]}"""
            : """{"name":"unit-test-index","fields":[{"name":"id","type":"Edm.String","key":true}]}""";
        var status = request.Method == HttpMethod.Post && !request.RequestUri.AbsolutePath.Contains("/docs/")
            ? HttpStatusCode.Created
            : HttpStatusCode.OK;
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    internal sealed class FixedServerRoleAccessor(ServerRole serverRole) : IServerRoleAccessor
    {
        public ServerRole CurrentServerRole => serverRole;
    }
}
