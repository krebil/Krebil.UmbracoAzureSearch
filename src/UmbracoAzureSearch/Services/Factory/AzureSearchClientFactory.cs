using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Options;
using Throw;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.IndexAliasResolver;

namespace UmbracoAzureSearch.Services.Factory;

public class AzureSearchClientFactory : IAzureSearchClientFactory
{
    private readonly UmbracoAzureSearchOptions _azureSearchOptions;
    private readonly IIndexAliasResolver _indexAliasResolver;
    private readonly TokenCredential? _tokenCredential;

    // One client for the factory's lifetime: it is thread-safe, and its pipeline caches the access token.
    private readonly Lazy<SearchIndexClient> _searchIndexClient;

    public AzureSearchClientFactory(IOptions<UmbracoAzureSearchOptions> azureSearchOptions, IIndexAliasResolver indexAliasResolver)
        : this(azureSearchOptions, indexAliasResolver, null)
    {
    }

    internal AzureSearchClientFactory(
        IOptions<UmbracoAzureSearchOptions> azureSearchOptions,
        IIndexAliasResolver indexAliasResolver,
        TokenCredential? tokenCredential)
    {
        _azureSearchOptions = azureSearchOptions.Value;
        _indexAliasResolver = indexAliasResolver;
        _tokenCredential = tokenCredential;
        _searchIndexClient = new Lazy<SearchIndexClient>(CreateSearchIndexClient);
    }

    public SearchIndexClient GetSearchIndexClient() => _searchIndexClient.Value;

    private SearchIndexClient CreateSearchIndexClient()
    {
        _azureSearchOptions.Endpoint.ThrowIfNull();
        var endpoint = new Uri(_azureSearchOptions.Endpoint);

        // Serverless services reject unpaged list calls and need the preview API
        // version that supports paging. Standard tiers stay on the stable version.
        var options = _azureSearchOptions.IsServerless
            ? new SearchClientOptions(SearchClientOptions.ServiceVersion.V2026_05_01_Preview)
            : new SearchClientOptions();

        // A credential passed in code wins over a configured key.
        if (_tokenCredential is not null)
        {
            return new SearchIndexClient(endpoint, _tokenCredential, options);
        }

        if (string.IsNullOrWhiteSpace(_azureSearchOptions.Key))
        {
            throw new InvalidOperationException(
                $"Azure AI Search needs either {UmbracoAzureSearchOptions.Name}:Key or a TokenCredential passed to AddUmbracoAzureSearch.");
        }

        return new SearchIndexClient(endpoint, new AzureKeyCredential(_azureSearchOptions.Key), options);
    }

    public SearchClient GetSearchClient(string indexAlias)
    {
        indexAlias = _indexAliasResolver.Resolve(indexAlias);
        var indexClient = GetSearchIndexClient();
        return indexClient.GetSearchClient(indexAlias);
    }
}