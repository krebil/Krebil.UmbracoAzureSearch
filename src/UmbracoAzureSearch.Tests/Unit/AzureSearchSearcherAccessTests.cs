using Umbraco.Cms.Search.Core.Models.Searching;

namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSearcherAccessTests
{
    private const string IndexAlias = "unit-test-index";
    private const string Unprotected = "00000000-0000-0000-0000-000000000000";

    [Test]
    public async Task OnlyFindsUnprotectedDocumentsWithoutAccessContext()
    {
        var filter = await SearchWith(null);

        Assert.That(filter, Does.Contain($"accessKeys/any(k: search.in(k, '{Unprotected}', ','))"));
    }

    [Test]
    public async Task OnlyFindsUnprotectedDocumentsWithoutPrincipal()
    {
        var filter = await SearchWith(new AccessContext(Guid.Empty, [Guid.NewGuid()]));

        Assert.That(filter, Does.Contain($"accessKeys/any(k: search.in(k, '{Unprotected}', ','))"));
    }

    [Test]
    public async Task CanFindDocumentsProtectedForMemberAndGroups()
    {
        var member = Guid.NewGuid();
        var group1 = Guid.NewGuid();
        var group2 = Guid.NewGuid();

        var filter = await SearchWith(new AccessContext(member, [group1, group2]));

        Assert.That(filter, Does.Contain($"accessKeys/any(k: search.in(k, '{Unprotected},{member:D},{group1:D},{group2:D}', ','))"));
    }

    [Test]
    public async Task CanFindDocumentsProtectedForMemberWithoutGroups()
    {
        var member = Guid.NewGuid();

        var filter = await SearchWith(new AccessContext(member, null));

        Assert.That(filter, Does.Contain($"accessKeys/any(k: search.in(k, '{Unprotected},{member:D}', ','))"));
    }

    [Test]
    public async Task CanBypassProtection()
    {
        var filter = await SearchWith(AccessContext.BypassProtection());

        Assert.That(filter, Does.Not.Contain("accessKeys"));
    }

    private static async Task<string?> SearchWith(AccessContext? accessContext)
    {
        var service = new FakeSearchService();
        await service.CreateSearcher().SearchAsync(IndexAlias, query: "test", accessContext: accessContext);
        return service.LastFilter();
    }
}
