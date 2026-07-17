// DeepSeek Copilot Proxy - Ultra-Low-Overhead Edition
// Uses direct HTTP proxying optimized for minimal allocations.
// Maintains full reasoning_content caching for multi-turn DeepSeek conversations.
// iqmeta GmbH | Otto Neff
// Version 2026.05.09

using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Config ──────────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
    .Build();

var section = config.GetRequiredSection("DeepSeek");
string API_KEY     = section["ApiKey"]     ?? throw new InvalidOperationException("DeepSeek:ApiKey is missing.");
string BASE_URL    = section["BaseUrl"]    ?? throw new InvalidOperationException("DeepSeek:BaseUrl is missing.");
string MODEL_PRO   = section["ModelPro"]   ?? throw new InvalidOperationException("DeepSeek:ModelPro is missing.");
int    PORT_PRO    = section.GetValue<int>("PortPro");
string MODEL_FLASH = section["ModelFlash"] ?? throw new InvalidOperationException("DeepSeek:ModelFlash is missing.");
int    PORT_FLASH  = section.GetValue<int>("PortFlash");

var apps = new List<Task>();

// ─── JSON Helpers ────────────────────────────────────────────────────
JsonSerializerOptions JsonOpts = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

// ─── Builder Pro ─────────────────────────────────────────────────────────
var builderPro = WebApplication.CreateSlimBuilder(args);
apps.Add(Proxy.CreateAndeRun(builderPro, BASE_URL, PORT_PRO, MODEL_PRO, API_KEY, JsonOpts));

// ─── Builder Flash ─────────────────────────────────────────────────────────
var builderFlash = WebApplication.CreateSlimBuilder(args);
apps.Add(Proxy.CreateAndeRun(builderFlash, BASE_URL, PORT_FLASH, MODEL_FLASH, API_KEY, JsonOpts));


await Task.WhenAll(apps);