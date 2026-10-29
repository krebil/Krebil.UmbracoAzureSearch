using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.DependencyInjection;

namespace UmbracoAzureSearch.Services.Scoring;

/// <summary>Calls every <see cref="IScoringProfileProvider"/> and merges their profiles; on a name collision the later provider wins.</summary>
internal sealed class ScoringProfileSources(IServiceProvider serviceProvider)
{
    public async Task<ScoringProfile[]> GetAsync(string indexAlias, CancellationToken cancellationToken = default)
    {
        var profiles = new Dictionary<string, ScoringProfile>();
        foreach (var provider in serviceProvider.GetServices<IScoringProfileProvider>())
        {
            foreach (var profile in await provider.GetScoringProfilesAsync(indexAlias, cancellationToken))
            {
                profiles[profile.Name] = profile;
            }
        }

        return profiles.Values.ToArray();
    }
}
