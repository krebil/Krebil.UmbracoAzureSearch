using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.NotificationHandlers;

/// <inheritdoc />
public class EnsureIndicesNotificationHandler(
    IAzureSearchIndexManager azureSearchIndexManager) : INotificationAsyncHandler<UmbracoApplicationStartingNotification>
{
    /// <inheritdoc />
    public Task HandleAsync(UmbracoApplicationStartingNotification notification, CancellationToken cancellationToken)
        => Task.WhenAll(
            azureSearchIndexManager.EnsureAsync(Umbraco.Cms.Search.Core.Constants.IndexAliases.PublishedContent),
            azureSearchIndexManager.EnsureAsync(Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftContent),
            azureSearchIndexManager.EnsureAsync(Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMedia),
            azureSearchIndexManager.EnsureAsync(Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMembers));
}