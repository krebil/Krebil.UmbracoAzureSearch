using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;

namespace UmbracoAzureSearch.Services.Factory;

public interface IAzureSearchClientFactory
{
    public SearchIndexClient GetSearchIndexClient();

    public SearchClient GetSearchClient(string indexAlias);
}