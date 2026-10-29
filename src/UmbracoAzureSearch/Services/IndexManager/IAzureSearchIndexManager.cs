namespace UmbracoAzureSearch.Services.IndexManager;

public interface IAzureSearchIndexManager
{
    Task EnsureAsync(string indexAlias);

    Task ResetAsync(string indexAlias); 

    /// <summary>
    /// Removes a scoring profile from every Azure index of an Umbraco index. The package never removes a provider's profile
    /// on its own. Removing <c>umbraco-boosts</c>, <c>relevanceBoost</c> or the current default profile throws.
    /// </summary>
    Task RemoveScoringProfileAsync(string indexAlias, string profileName);

    /// <summary>
    /// Calls the scoring profile providers again and inserts or updates their profiles on every Azure index of an Umbraco
    /// index, without a rebuild. Throws when a profile uses a field the index does not have or one of the wrong type.
    /// </summary>
    Task RefreshScoringProfilesAsync(string indexAlias);
}