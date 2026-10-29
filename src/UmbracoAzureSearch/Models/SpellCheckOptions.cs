namespace UmbracoAzureSearch.Models;

/// <summary>Spell check settings of one index, under <c>Indexes:&lt;alias&gt;:SpellCheck</c>.</summary>
public class SpellCheckOptions
{
    /// <summary>
    /// The query language of the Azure lexicon speller per culture, for example <c>"en": "en-us"</c>. A culture key matches
    /// exactly first, then by its language part, case-insensitive. A culture without a mapping gets no correction.
    /// </summary>
    public Dictionary<string, string> Cultures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
