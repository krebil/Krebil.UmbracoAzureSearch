namespace UmbracoAzureSearch.Tests.Unit;

[TestFixture]
public class AzureSearchSearcherPrefixTests
{
    private const string IndexAlias = "unit-test-index";

    [TestCase("boat", "boat*", "A word of letters gets the prefix suffix.")]
    [TestCase("été", "été*", "Letters outside ASCII are letters.")]
    [TestCase("x2026", "x2026*", "Digits are plain too.")]
    [TestCase("boat*", "boat*", "A manual wildcard stays as typed.")]
    [TestCase("2026-11-03", "2026-11-03", "A hyphenated token goes as typed, so the analyzer splits it as it split the indexed text.")]
    [TestCase("e-mail", "e-mail", "Same for a hyphenated word.")]
    [TestCase("boat trip", "boat trip", "Two or more words go as typed.")]
    [TestCase("\"boat trip\"", "\"boat trip\"", "Simple query syntax goes as typed.")]
    [TestCase("boat ", "boat*", "Whitespace around the word is trimmed first.")]
    public async Task AddsThePrefixSuffixOnlyToAPlainWord(string query, string searchText, string because)
    {
        var service = new FakeSearchService();

        await service.CreateSearcher().SearchAsync(IndexAlias, query: query, culture: "en-US");

        Assert.That(service.LastSearchBody().GetProperty("search").GetString(), Is.EqualTo(searchText), because);
    }
}
