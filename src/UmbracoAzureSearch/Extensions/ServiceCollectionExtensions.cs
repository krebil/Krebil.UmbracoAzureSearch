using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
        services.AddSingleton<IAzureSearchClientFactory, AzureSearchClientFactory>();
        return services.AddUmbracoAzureSearchCore(configuration);
    }

    /// <summary>
    /// Authenticates with a Microsoft Entra ID credential (e.g. <c>DefaultAzureCredential</c> for a managed identity) instead
    /// of <c>UmbracoAzureSearch:Key</c>. The service must allow role-based access, and the identity needs the
    /// Search Service Contributor and Search Index Data Contributor roles.
    /// </summary>
    public static IServiceCollection AddUmbracoAzureSearch(this IServiceCollection services, IConfiguration configuration, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        services.AddSingleton<IAzureSearchClientFactory>(sp => new AzureSearchClientFactory(
            sp.GetRequiredService<IOptions<UmbracoAzureSearchOptions>>(),
            sp.GetRequiredService<IIndexAliasResolver>(),
            credential));
        return services.AddUmbracoAzureSearchCore(configuration);
    }

    private static IServiceCollection AddUmbracoAzureSearchCore(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .Configure<UmbracoAzureSearchOptions>(configuration.GetSection(UmbracoAzureSearchOptions.Name))
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
        return builder.AddNotificationAsyncHandler<UmbracoApplicationStartingNotification, EnsureIndicesNotificationHandler>();
    }
    
    
    public static IUmbracoBuilder RebuildIndicesOnStartup(this IUmbracoBuilder builder)
    {
        return builder.AddNotificationHandler<UmbracoApplicationStartedNotification, RebuildIndicesNotificationHandler>();
    }
}