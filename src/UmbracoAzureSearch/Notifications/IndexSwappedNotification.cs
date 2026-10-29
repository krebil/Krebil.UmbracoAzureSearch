using Umbraco.Cms.Core.Notifications;

namespace UmbracoAzureSearch.Notifications;

/// <summary>
/// Published once per Umbraco index after a zero-downtime rebuild moved all its Azure aliases to the new indexes. Searches
/// now read the new indexes, so this is the place to clear caches of search results. Not published when the swap is
/// skipped or fails. The old indexes still exist when the handlers run; the package deletes them after the handlers.
/// An exception from a handler is logged and does not stop the swap or the delete.
/// </summary>
/// <param name="IndexAlias">The Umbraco index alias, for example <c>Umb_PublishedContent</c>.</param>
/// <param name="Swaps">The Azure aliases that moved: one, or one per culture index with <c>IndexPerCulture</c>.</param>
public sealed record IndexSwappedNotification(string IndexAlias, IReadOnlyList<AzureIndexSwap> Swaps) : INotification;

/// <summary>One Azure alias that a zero-downtime rebuild moved.</summary>
/// <param name="AzureAlias">The Azure alias that searches use, for example <c>umb-publishedcontent</c>.</param>
/// <param name="OldIndex">
/// The Azure index the alias pointed to before, or null when there was none. At the first migration it is the plain index
/// that the alias replaced, and that index is already deleted.
/// </param>
/// <param name="NewIndex">The Azure index the alias points to now, for example <c>umb-publishedcontent-b</c>.</param>
public sealed record AzureIndexSwap(string AzureAlias, string? OldIndex, string NewIndex);
