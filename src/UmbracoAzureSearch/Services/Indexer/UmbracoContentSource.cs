using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace UmbracoAzureSearch.Services.Indexer;

/// <summary>
/// Reads content types and content items from Umbraco, for content scope and per-index indexers.
/// </summary>
internal interface IUmbracoContentSource
{
    /// <summary>The key of the content type with this alias, for the object type of the document.</summary>
    Guid? GetContentTypeKey(string alias, UmbracoObjectTypes objectType);

    /// <summary>The content item, media item or member.</summary>
    IContentBase? GetContent(Guid id, UmbracoObjectTypes objectType);

    /// <summary>The ISO codes of the Umbraco languages.</summary>
    Task<string[]> GetLanguageIsoCodesAsync();

    /// <summary>True when the content type with this key varies by segment. Umbraco caches the content types.</summary>
    bool VariesBySegment(Guid contentTypeKey, UmbracoObjectTypes objectType) => false;
}

// The Umbraco services are resolved on first use, so hosts without them (tests) only need them when the definition does.
internal sealed class UmbracoContentSource(IServiceProvider serviceProvider) : IUmbracoContentSource
{
    public Guid? GetContentTypeKey(string alias, UmbracoObjectTypes objectType) => objectType switch
    {
        UmbracoObjectTypes.Document => serviceProvider.GetRequiredService<IContentTypeService>().Get(alias)?.Key,
        UmbracoObjectTypes.Media => serviceProvider.GetRequiredService<IMediaTypeService>().Get(alias)?.Key,
        UmbracoObjectTypes.Member => serviceProvider.GetRequiredService<IMemberTypeService>().Get(alias)?.Key,
        _ => null,
    };

    public IContentBase? GetContent(Guid id, UmbracoObjectTypes objectType) => objectType switch
    {
        UmbracoObjectTypes.Document => serviceProvider.GetRequiredService<IContentService>().GetById(id),
        UmbracoObjectTypes.Media => serviceProvider.GetRequiredService<IMediaService>().GetById(id),
        UmbracoObjectTypes.Member => serviceProvider.GetRequiredService<IMemberService>().GetById(id),
        _ => null,
    };

    public bool VariesBySegment(Guid contentTypeKey, UmbracoObjectTypes objectType) => objectType switch
    {
        UmbracoObjectTypes.Document => serviceProvider.GetRequiredService<IContentTypeService>().Get(contentTypeKey)?.VariesBySegment(),
        UmbracoObjectTypes.Media => serviceProvider.GetRequiredService<IMediaTypeService>().Get(contentTypeKey)?.VariesBySegment(),
        UmbracoObjectTypes.Member => serviceProvider.GetRequiredService<IMemberTypeService>().Get(contentTypeKey)?.VariesBySegment(),
        _ => null,
    } is true;

    public async Task<string[]> GetLanguageIsoCodesAsync()
        => (await serviceProvider.GetRequiredService<ILanguageService>().GetAllAsync()).Select(l => l.IsoCode).ToArray();
}
