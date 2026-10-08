using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Throw;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Services;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
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
        => services.AddUmbracoAzureSearchCore(configuration, null);

    /// <summary>
    /// Authenticates with a Microsoft Entra ID credential (e.g. <c>DefaultAzureCredential</c> for a managed identity) instead
    /// of <c>UmbracoAzureSearch:Key</c>. The service must allow role-based access, and the identity needs the
    /// Search Service Contributor and Search Index Data Contributor roles.
    /// </summary>
    public static IServiceCollection AddUmbracoAzureSearch(this IServiceCollection services, IConfiguration configuration, TokenCredential credential)
        => services.AddUmbracoAzureSearchCore(configuration, credential.ThrowIfNull());

    private static IServiceCollection AddUmbracoAzureSearchCore(this IServiceCollection services, IConfiguration configuration, TokenCredential? credential)
    {
        services
            .Configure<UmbracoAzureSearchOptions>(configuration.GetSection(UmbracoAzureSearchOptions.Name))
            .AddSingleton<IAzureSearchClientFactory>(sp => new AzureSearchClientFactory(
                sp.GetRequiredService<IOptions<UmbracoAzureSearchOptions>>(),
                sp.GetRequiredService<IIndexAliasResolver>(),
                credential))
            .AddSingleton<IIndexAliasResolver, IndexAliasResolver>()
            .AddSingleton<IAzureSearchIndexManager, AzureSearchIndexManager>()
            .AddSingleton<IAzureSearchIndexer, AzureSearchIndexer>()
            .AddSingleton<DocumentMapper>()
            .AddTransient<IAzureSearchSearcher, AzureSearchSearcher>();
        
        services.AddSingleton<ISearcher, AzureSearchSearcher>();
        services.AddSingleton<IIndexer, AzureSearchIndexer>();

        // Umbraco Search only indexes and searches aliases that are registered, so register the four default indexes
        // the way the Examine provider does. A host can still override or add registrations after this call.
        services.Configure<IndexOptions>(options =>
        {
            options.RegisterContentIndex<IAzureSearchIndexer, IAzureSearchSearcher, IDraftContentChangeStrategy>(
                Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftContent, UmbracoObjectTypes.Document);
            options.RegisterContentIndex<IAzureSearchIndexer, IAzureSearchSearcher, IPublishedContentChangeStrategy>(
                Umbraco.Cms.Search.Core.Constants.IndexAliases.PublishedContent, UmbracoObjectTypes.Document);
            options.RegisterContentIndex<IAzureSearchIndexer, IAzureSearchSearcher, IDraftContentChangeStrategy>(
                Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMedia, UmbracoObjectTypes.Media);
            options.RegisterContentIndex<IAzureSearchIndexer, IAzureSearchSearcher, IDraftContentChangeStrategy>(
                Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMembers, UmbracoObjectTypes.Member);
        });

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