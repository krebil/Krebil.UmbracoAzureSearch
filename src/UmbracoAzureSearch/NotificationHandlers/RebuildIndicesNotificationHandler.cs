using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;

namespace UmbracoAzureSearch.NotificationHandlers;

/// <inheritdoc />
public class RebuildIndicesNotificationHandler(  IContentIndexingService contentIndexingService) : INotificationHandler<UmbracoApplicationStartedNotification>
{
    /// <inheritdoc />
    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        contentIndexingService.Rebuild((Umbraco.Cms.Search.Core.Constants.IndexAliases.PublishedContent));
        contentIndexingService.Rebuild((Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftContent));
        //contentIndexingService.Rebuild((Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMedia));
        //contentIndexingService.Rebuild((Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftMembers));
    } 
}
