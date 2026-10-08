using Umbraco.Cms.Core.Sync;

namespace UmbracoAzureSearch.Services;

public abstract class UmbracoAzureServiceBase(IServerRoleAccessor serverRoleAccessor)
{
    /// <summary>
    /// Determines whether the current server role should avoid manipulating indexes.
    /// </summary>
    /// <returns>True if the current server role is a subscriber, indicating that indexes should not be manipulated; otherwise, false.</returns>
    protected bool ShouldNotManipulateIndexes() => serverRoleAccessor.CurrentServerRole is ServerRole.Subscriber;
}