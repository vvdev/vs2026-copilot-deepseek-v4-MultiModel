using System.Text.Json.Serialization;

namespace CopilotOllamaProxy.AiProviders.DeepSeekV4;

public class DeepSeekV4ProviderSettings : AiProviderSettings<DeepSeekV4ModelSettings>
{
    [JsonPropertyName("Models")]
    public List<DeepSeekV4ModelSettings> ModelEntries { get; init; } = new();

    public override string ProviderType { get; init; } = "DeepSeekV4";
    public override string ApiKey { get; init; } = string.Empty;
    public override string BaseUrl { get; init; } = string.Empty;

    [JsonIgnore]
    public override IReadOnlyList<AiModelSettings> Models => ModelEntries;

    [JsonIgnore]
    public override IReadOnlyList<DeepSeekV4ModelSettings> TypedModels => ModelEntries;
}
