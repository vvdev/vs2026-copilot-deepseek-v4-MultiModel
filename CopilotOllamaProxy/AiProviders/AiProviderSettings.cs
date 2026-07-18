using CopilotOllamaProxy.AiProviders.DeepSeekV4;

using System.Text.Json.Serialization;

namespace CopilotOllamaProxy.AiProviders;

public interface IProviderServiceSettings
{
    string ProviderType { get; }
    string ApiKey { get; }
    string BaseUrl { get; }
    IReadOnlyList<AiModelSettings> Models { get; }
}

[JsonDerivedType(typeof(DeepSeekV4ProviderSettings), typeDiscriminator: "DeepSeekV4")]
public abstract class ServiceProviderSettingsBase : IProviderServiceSettings
{
    public abstract string ProviderType { get; init; }
    public abstract string ApiKey { get; init; }
    public abstract string BaseUrl { get; init; }
    public abstract IReadOnlyList<AiModelSettings> Models { get; }
}

public abstract class AiProviderSettings<TModel> : ServiceProviderSettingsBase
    where TModel : AiModelSettings
{
    public abstract IReadOnlyList<TModel> TypedModels { get; }
}
