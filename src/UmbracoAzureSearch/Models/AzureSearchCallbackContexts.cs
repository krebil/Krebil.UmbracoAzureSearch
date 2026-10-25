namespace UmbracoAzureSearch.Models;

/// <summary>Which Azure index a <c>ConfigureIndex</c> callback configures.</summary>
/// <param name="IndexAlias">The Umbraco index alias.</param>
/// <param name="IndexName">The name of the Azure index.</param>
/// <param name="Culture">The culture key of an IndexPerCulture culture index, for example <c>fr</c>. Null otherwise.</param>
/// <param name="IsFallback">True for the fallback index of IndexPerCulture.</param>
public sealed record AzureSearchIndexContext(string IndexAlias, string IndexName, string? Culture, bool IsFallback);

/// <summary>The Umbraco search a <c>ConfigureSearch</c> callback changes the Azure query of.</summary>
/// <param name="IndexAlias">The Umbraco index alias.</param>
/// <param name="IndexName">The name of the Azure index the query goes to.</param>
/// <param name="Query">The query text as the caller passed it.</param>
/// <param name="Culture">The culture as the caller passed it, for example <c>fr-FR</c>. Null for no culture.</param>
/// <param name="Segment">The segment as the caller passed it. Null for no segment.</param>
public sealed record AzureSearchRequest(string IndexAlias, string IndexName, string? Query, string? Culture, string? Segment);
