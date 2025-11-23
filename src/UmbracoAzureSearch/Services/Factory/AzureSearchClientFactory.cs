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
        return new SearchIndexClient(new Uri(_azureSearchOptions.Endpoint),
            new AzureKeyCredential(_azureSearchOptions.Key));
    }

    public SearchClient GetSearchClient(string indexAlias)
    {
        indexAlias = indexAliasResolver.Resolve(indexAlias);
        var indexClient = GetSearchIndexClient();
        return indexClient.GetSearchClient(indexAlias);
    }
}