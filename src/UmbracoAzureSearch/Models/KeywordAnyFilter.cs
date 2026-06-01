using Umbraco.Cms.Search.Core.Models.Searching.Filtering;

namespace UmbracoAzureSearch.Models;

/// <summary>
/// Filters documents based on whether a keyword field has any values.
/// Translates to the OData expression <c>{field}_keywords/any()</c> or
/// <c>not ({field}_keywords/any())</c> when <see cref="Filter.Negate"/> is true.
/// </summary>
public record KeywordAnyFilter(string FieldName, bool Negate = false) : Filter(FieldName, Negate);
