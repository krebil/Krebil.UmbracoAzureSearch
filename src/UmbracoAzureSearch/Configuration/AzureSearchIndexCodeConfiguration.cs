namespace UmbracoAzureSearch.Configuration;

/// <summary>One <c>ConfigureAzureSearchIndex</c> call, applied after appsettings in registration order.</summary>
internal sealed record AzureSearchIndexCodeConfiguration(string IndexAlias, Action<AzureSearchIndexBuilder> Configure);
