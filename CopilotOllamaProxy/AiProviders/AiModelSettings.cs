namespace CopilotOllamaProxy.AiProviders;

public abstract class AiModelSettings
{
    public string CopilotId { get; init; }
    public string UnderlyingId { get; init; }
}
