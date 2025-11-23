using Umbraco.Cms.Core.Sync;

namespace UmbracoAzureSearch.Services;

public abstract class UmbracoAzureServiceBase(IServerRoleAccessor serverRoleAccessor)
{
    protected bool ShouldNotManipulateIndexes() => serverRoleAccessor.CurrentServerRole is ServerRole.Subscriber;
}