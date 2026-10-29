using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using UmbracoAzureSearch.Models;
using UmbracoAzureSearch.Services.IndexAliasResolver;
using UmbracoAzureSearch.Services.Indexer;

namespace UmbracoAzureSearch.Services.IndexManager;

/// <summary>
/// One Azure index behind an Umbraco index alias. <see cref="Analyzer"/> is the text analyzer of the index; null uses
/// <c>standard.lucene</c>.
/// </summary>
internal sealed record PhysicalIndex(string Name, string? Analyzer);

/// <summary>
/// Resolves an Umbraco index alias to its Azure indexes. Without <see cref="AnalyzerMode.IndexPerCulture"/> an alias has one
/// index. With it, the alias has <c>&lt;alias&gt;-&lt;culture&gt;</c> per mapped culture, plus <c>&lt;alias&gt;-fallback</c>
/// when Umbraco has a language outside the map.
/// </summary>
internal sealed class PhysicalIndexes(
    IIndexAliasResolver indexAliasResolver,
    IUmbracoContentSource contentSource,
    ILogger<PhysicalIndexes> logger)
{
    public const string FallbackSuffix = "fallback";

    /// <summary>Every index of the alias. The fallback index is included when Umbraco has a language outside the map.</summary>
    public async Task<PhysicalIndex[]> AllAsync(string indexAlias, IndexDefinition definition)
    {
        if (!definition.IndexPerCulture)
            return [Single(indexAlias)];

        var indexes = Mapped(indexAlias, definition).ToList();
        if (await FallbackNeededAsync(definition) is not false)
        {
            indexes.Add(Fallback(indexAlias));
        }

        return indexes.ToArray();
    }

    /// <summary>
    /// The index that holds a culture: the culture index, or the fallback index for an unmapped culture.
    /// Without IndexPerCulture, the one index of the alias.
    /// </summary>
    public PhysicalIndex ForCulture(string indexAlias, IndexDefinition definition, string culture)
    {
        if (!definition.IndexPerCulture)
            return Single(indexAlias);

        var key = IndexDefinition.MatchKey(definition.Options.Analyzers.Cultures.Keys, culture);
        return key is null
            ? Fallback(indexAlias)
            : new PhysicalIndex(Name(indexAlias, key), definition.Options.Analyzers.Cultures[key]);
    }

    /// <summary>The fallback index of the alias.</summary>
    public PhysicalIndex Fallback(string indexAlias)
        => new(Name(indexAlias, FallbackSuffix), LexicalAnalyzerName.StandardLucene.ToString());

    /// <summary>
    /// True when Umbraco has a language outside the culture map, so the alias needs a fallback index. Null when the
    /// languages cannot be read, for example before Umbraco is installed.
    /// </summary>
    public async Task<bool?> FallbackNeededAsync(IndexDefinition definition)
    {
        try
        {
            var languages = await contentSource.GetLanguageIsoCodesAsync();
            return languages.Any(language => IndexDefinition.MatchKey(definition.Options.Analyzers.Cultures.Keys, language) is null);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read the Umbraco languages to decide on a fallback index");
            return null;
        }
    }

    private IEnumerable<PhysicalIndex> Mapped(string indexAlias, IndexDefinition definition)
        => definition.Options.Analyzers.Cultures.Select(culture => new PhysicalIndex(Name(indexAlias, culture.Key), culture.Value));

    private PhysicalIndex Single(string indexAlias) => new(indexAliasResolver.Resolve(indexAlias), null);

    private string Name(string indexAlias, string suffix) => $"{indexAliasResolver.Resolve(indexAlias)}-{suffix.ToLowerInvariant()}";
}
