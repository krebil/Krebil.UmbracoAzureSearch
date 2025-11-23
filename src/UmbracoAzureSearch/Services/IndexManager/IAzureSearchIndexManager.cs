namespace UmbracoAzureSearch.Services.IndexManager;

public interface IAzureSearchIndexManager
{
    Task EnsureAsync(string indexAlias);

    Task ResetAsync(string indexAlias); 
}