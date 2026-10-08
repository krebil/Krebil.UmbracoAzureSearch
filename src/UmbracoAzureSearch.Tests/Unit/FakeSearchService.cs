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

// Answers every Azure AI Search call with an empty result and records the requests.
internal sealed class FakeSearchService : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

    public AzureSearchClientFactory CreateFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UmbracoAzureSearch:Endpoint"] = "https://unit-test.search.windows.net",
                ["UmbracoAzureSearch:Key"] = "test-key",
            })
            .Build();

        var factory = (AzureSearchClientFactory)new ServiceCollection()
            .AddUmbracoAzureSearch(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IAzureSearchClientFactory>();
        factory.Transport = new HttpClientTransport(new HttpClient(this));
        return factory;
    }

    public AzureSearchSearcher CreateSearcher(ServerRole serverRole = ServerRole.Single)
        => new(new FixedServerRoleAccessor(serverRole), CreateFactory());

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

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"@odata.count":0,"value":[]}""", Encoding.UTF8, "application/json")
        };
    }

    internal sealed class FixedServerRoleAccessor(ServerRole serverRole) : IServerRoleAccessor
    {
        public ServerRole CurrentServerRole => serverRole;
    }
}
