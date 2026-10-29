using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.DeliveryApi;
using UmbracoAzureSearch.Models;

namespace UmbracoAzureSearch.Configuration;

/// <summary>
/// Fills <see cref="AzureSearchIndexOptions.IndexHandlerFields"/> from the registered Delivery API index handlers, so their
/// fields stay in the index when automatic fields are off.
/// </summary>
internal sealed class ConfigureIndexHandlerFields(IServiceProvider serviceProvider) : IPostConfigureOptions<AzureSearchIndexOptions>
{
    public void PostConfigure(string? name, AzureSearchIndexOptions options)
    {
        if (serviceProvider.GetService<ContentIndexHandlerCollection>() is not { } handlers)
            return;

        // Umbraco Search skips the core handlers, because the system fields (Umb_*) cover them.
        options.IndexHandlerFields = handlers
            .Where(handler => handler.GetType().Namespace?.StartsWith("Umbraco.Cms.Api.Delivery") is not true)
            .SelectMany(handler => handler.GetFields())
            .DistinctBy(field => field.FieldName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(field => field.FieldName, field => field.FieldType, StringComparer.OrdinalIgnoreCase);
    }
}
