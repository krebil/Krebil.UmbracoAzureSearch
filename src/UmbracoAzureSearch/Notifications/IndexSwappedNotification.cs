using Umbraco.Cms.Core.Notifications;

namespace UmbracoAzureSearch.Notifications;

/// <summary>
/// Published once per Umbraco index after a zero-downtime rebuild moved all its Azure aliases to the new indexes. Searches
/// now read the new indexes. Use the notification to clear caches of search results. Not published when the swap is
/// skipped or fails. The package deletes the old indexes after the handlers ran. The package logs an exception from a
/// handler, and the swap and the delete continue.
/// </summary>
/// <param name="IndexAlias">The Umbraco index alias, for example <c>Umb_PublishedContent</c>.</param>
/// <param name="Swaps">The Azure aliases that moved. With <c>IndexPerCulture</c>, one per culture index and the fallback index.</param>
public sealed record IndexSwappedNotification(string IndexAlias, IReadOnlyList<AzureIndexSwap> Swaps) : INotification;

/// <summary>One Azure alias that a zero-downtime rebuild moved.</summary>
/// <param name="AzureAlias">The Azure alias that searches use, for example <c>umb-publishedcontent</c>.</param>
/// <param name="OldIndex">
/// The Azure index the alias pointed to before, or null when there was none. At the first migration it is the plain index
/// that the alias replaced, and that index is already deleted.
/// </param>
/// <param name="NewIndex">The Azure index the alias points to now, for example <c>umb-publishedcontent-b</c>.</param>
public sealed record AzureIndexSwap(string AzureAlias, string? OldIndex, string NewIndex);
