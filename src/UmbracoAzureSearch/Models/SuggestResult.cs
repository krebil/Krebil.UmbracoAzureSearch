using Umbraco.Cms.Core.Models;

namespace UmbracoAzureSearch.Models;

/// <summary>The documents that <c>SuggestAsync</c> suggests for a partial query.</summary>
public record SuggestResult(IEnumerable<SuggestedDocument> Documents);

/// <summary>One suggested document.</summary>
/// <param name="Id">The key of the content item, media item or member.</param>
/// <param name="ObjectType">The Umbraco object type of the document.</param>
/// <param name="Text">The matched suggestion text, with the highlight tags around the match. It can contain HTML: encode it before rendering.</param>
/// <param name="Fields">The fields named in <c>select</c>, by the names the caller used. A field the document has no value for is null.</param>
public record SuggestedDocument(Guid Id, UmbracoObjectTypes ObjectType, string Text, IReadOnlyDictionary<string, object?> Fields);
