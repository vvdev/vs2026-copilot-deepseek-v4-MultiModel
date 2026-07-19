// DeepSeek Copilot Proxy - Ultra-Low-Overhead Edition
// Uses direct HTTP proxying optimized for minimal allocations.
// Maintains full reasoning_content caching for multi-turn DeepSeek conversations.
// iqmeta GmbH | Otto Neff
// Version 2026.05.09

using CopilotOllamaProxy.AiProviders;
using CopilotOllamaProxy.AiProviders.DeepSeekV4;
using CopilotOllamaProxy;

using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

// ─── Tray icon + hidden console ───────────────────────────────────────
TrayManager.Start();

// ─── Config ──────────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
    .Build();

var aiProviders = config.GetRequiredSection("AiProviders")
    .Get<AiProvidersConfiguration>() ?? throw new InvalidOperationException("AiProviders section is missing.");

var providerSettings = aiProviders.ToProviderSettingsList();

var apps = new List<Task>();

// ─── JSON Helpers ────────────────────────────────────────────────────
JsonSerializerOptions JsonOpts = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// ─── Launch Proxy Instances ──────────────────────────────────────────
int totalModels = 0;
foreach (var provider in providerSettings)
{
    switch (provider)
    {
        case DeepSeekV4ProviderSettings ds:
            foreach (var model in ds.TypedModels)
            {
                var builder = WebApplication.CreateSlimBuilder(args);
                apps.Add(DeepSeekV4Proxy.CreateAndeRun(
                    builder, ds.BaseUrl, ds.ApiKey, model, JsonOpts));
                totalModels++;
            }
            break;
        default:
            Console.WriteLine($"Warning: Unknown provider type '{provider.ProviderType}' — skipping.");
            break;
    }
}
TrayManager.SetRunningModelsCount(totalModels);

await Task.WhenAll(apps);

Application.Exit();
Environment.Exit(0);
