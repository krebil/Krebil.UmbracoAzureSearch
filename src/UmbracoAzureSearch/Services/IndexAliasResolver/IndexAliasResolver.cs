
using Microsoft.Extensions.Options;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Services.IndexAliasResolver;

public class IndexAliasResolver : IIndexAliasResolver
{
    private readonly string? _environment;

    public IndexAliasResolver(IOptions<UmbracoAzureSearchOptions> options)
        => _environment = options.Value.Environment;

    public string Resolve(string indexAlias)
        => ValidIndexAlias(_environment is null ? indexAlias : $"{indexAlias}_{_environment}");

    private static string ValidIndexAlias(string indexAlias)
        => indexAlias.Replace("_","-").ToLowerInvariant();
}