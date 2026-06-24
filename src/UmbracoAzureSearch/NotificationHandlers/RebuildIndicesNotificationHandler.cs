using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;

namespace UmbracoAzureSearch.NotificationHandlers;

/// <inheritdoc />
public class RebuildIndicesNotificationHandler(IContentIndexingService contentIndexingService, IOriginProvider originProvider) : INotificationHandler<UmbracoApplicationStartedNotification>
{
    /// <inheritdoc />
    public void Handle(UmbracoApplicationStartedNotification notification)
    {
        var origin = originProvider.GetCurrent();
        contentIndexingService.Rebuild(Umbraco.Cms.Search.Core.Constants.IndexAliases.PublishedContent, origin);
        contentIndexingService.Rebuild(Umbraco.Cms.Search.Core.Constants.IndexAliases.DraftContent, origin);
    }
}
