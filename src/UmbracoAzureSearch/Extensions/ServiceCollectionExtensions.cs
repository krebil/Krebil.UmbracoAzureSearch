using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services;
using UmbracoAzureSearch.Services.Factory;
using UmbracoAzureSearch.Services.IndexAliasResolver;
using UmbracoAzureSearch.Services.Indexer;
using UmbracoAzureSearch.Services.IndexManager;
using UmbracoAzureSearch.Services.Searcher;

namespace UmbracoAzureSearch.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddUmbracoAzureSearch(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .Configure<UmbracoAzureSearchOptions>(configuration.GetSection(UmbracoAzureSearchOptions.Name))
            .AddSingleton<IAzureSearchClientFactory, AzureSearchClientFactory>()
            .AddSingleton<IIndexAliasResolver, IndexAliasResolver>()
            .AddSingleton<IAzureSearchIndexManager, AzureSearchIndexManager>()
            .AddSingleton<IAzureSearchIndexer, AzureSearchIndexer>()
            .AddSingleton<DocumentMapper>()
            .AddTransient<IAzureSearchSearcher, AzureSearchSearcher>();
        return services;
    }
}