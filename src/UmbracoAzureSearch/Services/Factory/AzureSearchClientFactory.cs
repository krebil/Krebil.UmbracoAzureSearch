using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Options;
using Throw;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.IndexAliasResolver;

namespace UmbracoAzureSearch.Services.Factory;

public class AzureSearchClientFactory(IOptions<UmbracoAzureSearchOptions> azureSearchOptions, IIndexAliasResolver indexAliasResolver)
    : IAzureSearchClientFactory
{
    private readonly UmbracoAzureSearchOptions _azureSearchOptions = azureSearchOptions.Value;

    public SearchIndexClient GetSearchIndexClient()
    {
        _azureSearchOptions.Endpoint.ThrowIfNull();
        _azureSearchOptions.Key.ThrowIfNull();
        var endpoint = new Uri(_azureSearchOptions.Endpoint);
        var credential = new AzureKeyCredential(_azureSearchOptions.Key);

        // Serverless services reject unpaged list calls and need the preview API
        // version that supports paging. Standard tiers stay on the stable version.
        if (_azureSearchOptions.IsServerless)
        {
            var options = new SearchClientOptions(SearchClientOptions.ServiceVersion.V2026_05_01_Preview);
            return new SearchIndexClient(endpoint, credential, options);
        }

        return new SearchIndexClient(endpoint, credential);
    }

    public SearchClient GetSearchClient(string indexAlias)
    {
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var indexClient = GetSearchIndexClient();
        return indexClient.GetSearchClient(indexAlias);
    }
}