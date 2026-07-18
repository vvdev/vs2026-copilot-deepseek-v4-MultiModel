using CopilotOllamaProxy.AiProviders.DeepSeekV4;

namespace CopilotOllamaProxy.AiProviders;

public class AiProvidersConfiguration
{
    public List<ProviderEntryDto> Entries { get; init; } = new();

    public List<IProviderServiceSettings> ToProviderSettingsList()
    {
        var result = new List<IProviderServiceSettings>();
        foreach (var entry in Entries)
        {
            result.Add(entry switch
            {
                { ProviderType: "DeepSeekV4" } => new DeepSeekV4ProviderSettings
                {
                    ApiKey = entry.ApiKey,
                    BaseUrl = entry.BaseUrl,
                    ModelEntries = entry.Models.ToList()
                },
                _ => throw new InvalidOperationException(
                    $"Unknown provider type: {entry.ProviderType}")
            });
        }
        return result;
    }
}

public class ProviderEntryDto
{
    public string ProviderType { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public List<DeepSeekV4ModelSettings> Models { get; init; } = new();
}
