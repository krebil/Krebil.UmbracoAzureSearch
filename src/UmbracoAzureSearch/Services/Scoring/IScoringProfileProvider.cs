using Azure.Search.Documents.Indexes.Models;

namespace UmbracoAzureSearch.Services.Scoring;

/// <summary>
/// Gives scoring profiles to the Azure indexes of an Umbraco index. Register it with <c>AddScoringProfileProvider&lt;T&gt;()</c>.
/// The package inserts and updates the profiles; it removes one only through <c>RemoveScoringProfileAsync</c>.
/// </summary>
public interface IScoringProfileProvider
{
    /// <summary>
    /// The profiles of an index. Called per Umbraco index alias; return no profiles for indexes this provider does not handle.
    /// Field names are Azure field names, for example <c>title_texts</c> or <c>date_datetimeoffsets_sort</c>.
    /// </summary>
    Task<IEnumerable<ScoringProfile>> GetScoringProfilesAsync(string indexAlias, CancellationToken cancellationToken);
}
