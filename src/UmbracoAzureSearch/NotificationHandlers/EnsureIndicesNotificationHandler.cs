using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using UmbracoAzureSearch.Services.IndexManager;

namespace UmbracoAzureSearch.NotificationHandlers;

/// <inheritdoc />
public class EnsureIndicesNotificationHandler(
    IAzureSearchIndexManager azureSearchIndexManager) : INotificationHandler<UmbracoApplicationStartingNotification>
{
    /// <inheritdoc />
    public void Handle(UmbracoApplicationStartingNotification notification)
    {
        azureSearchIndexManager.EnsureAsync((Umbraco.Cms.Search.Core.Constants.IndexAliases.PublishedContent));
        azureSearchIndexManager.EnsureAsync((Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftContent));
        azureSearchIndexManager.EnsureAsync((Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMedia));
        azureSearchIndexManager.EnsureAsync((Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMembers));
    } 
}