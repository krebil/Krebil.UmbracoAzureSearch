namespace UmbracoAzureSearch.Models;

public class UmbracoAzureSearchOptions
{
    public const string Name = "UmbracoAzureSearch";
    
    public string? Key { get; set; }
    public string? Endpoint { get; set; }
    
    public bool EnableDebugMode { get; set; }

    public string? Environment { get; set; }
}