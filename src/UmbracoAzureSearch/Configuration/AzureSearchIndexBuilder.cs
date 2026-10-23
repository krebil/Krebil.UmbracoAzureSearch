using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Configuration;

/// <summary>
/// Sets the options of one index in code. Runs after appsettings, so a value set here wins.
/// </summary>
public sealed class AzureSearchIndexBuilder
{
    private readonly AzureSearchIndexOptions _options;

    internal AzureSearchIndexBuilder(AzureSearchIndexOptions options) => _options = options;

    /// <summary>Turns suggestions on or off for the index. Same as <c>Suggestions:Enabled</c>.</summary>
    public AzureSearchIndexBuilder Suggestions(bool enabled)
    {
        _options.Suggestions.Enabled = enabled;
        return this;
    }

    /// <summary>The Umbraco fields whose texts feed suggestions. Same as <c>Suggestions:Fields</c>.</summary>
    public AzureSearchIndexBuilder SuggestionFields(params string[] fields)
    {
        _options.Suggestions.Fields = fields;
        return this;
    }
}
