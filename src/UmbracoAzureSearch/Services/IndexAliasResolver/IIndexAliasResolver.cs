namespace UmbracoAzureSearch.Services.IndexAliasResolver;


    public interface IIndexAliasResolver
    {
        string Resolve(string indexAlias);
    }