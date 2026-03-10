using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Search.Core.Services;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.NotificationHandlers;
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
        
        services.AddSingleton<ISearcher, AzureSearchSearcher>();
        services.AddSingleton<IIndexer, AzureSearchIndexer>();
        
        return services;
    }

    public static IUmbracoBuilder EnsureIndicesOnStartup(this IUmbracoBuilder builder)
    {
        return builder.AddNotificationHandler<UmbracoApplicationStartingNotification, EnsureIndicesNotificationHandler>();
    }
    
    
    public static IUmbracoBuilder RebuildIndicesOnStartup(this IUmbracoBuilder builder)
    {
        return builder.AddNotificationHandler<UmbracoApplicationStartedNotification, RebuildIndicesNotificationHandler>();
    }
}