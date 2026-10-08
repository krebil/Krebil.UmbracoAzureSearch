using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Options;
using Throw;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.IndexAliasResolver;

namespace UmbracoAzureSearch.Services.Factory;

public class AzureSearchClientFactory(
    IOptions<UmbracoAzureSearchOptions> azureSearchOptions,
    IIndexAliasResolver indexAliasResolver,
    TokenCredential? tokenCredential = null)
    : IAzureSearchClientFactory
{
    private readonly UmbracoAzureSearchOptions _azureSearchOptions = azureSearchOptions.Value;

    // One client for the factory's lifetime: it is thread-safe, and its pipeline caches the access token.
    private SearchIndexClient? _searchIndexClient;

    // Lets tests inspect requests without a network.
    internal HttpPipelineTransport? Transport { get; set; }

    public SearchIndexClient GetSearchIndexClient()
        => LazyInitializer.EnsureInitialized(ref _searchIndexClient, CreateSearchIndexClient);

    private SearchIndexClient CreateSearchIndexClient()
    {
        _azureSearchOptions.Endpoint.ThrowIfNull();
        var endpoint = new Uri(_azureSearchOptions.Endpoint);

        // Serverless services reject unpaged list calls and need the preview API
        // version that supports paging. Standard tiers stay on the stable version.
        var options = _azureSearchOptions.IsServerless
            ? new SearchClientOptions(SearchClientOptions.ServiceVersion.V2026_05_01_Preview)
            : new SearchClientOptions();

        if (Transport is not null)
        {
            options.Transport = Transport;
        }

        // A credential passed in code wins over a configured key.
        if (tokenCredential is not null)
        {
            return new SearchIndexClient(endpoint, tokenCredential, options);
        }

        string key = _azureSearchOptions.Key
            .ThrowIfNull(() => new InvalidOperationException(
                $"Azure AI Search needs either {UmbracoAzureSearchOptions.Name}:Key or a TokenCredential passed to AddUmbracoAzureSearch."))
            .IfWhiteSpace();

        return new SearchIndexClient(endpoint, new AzureKeyCredential(key), options);
    }

    public SearchClient GetSearchClient(string indexAlias)
    {
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var indexClient = GetSearchIndexClient();
        return indexClient.GetSearchClient(indexAlias);
    }
}
