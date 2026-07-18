# Visual Studio 2026 Copilot — Bring Your Own Model Configuration

> Reverse-engineered from VS 2026 Copilot DLLs (`Microsoft.VisualStudio.Copilot.Core.dll`, `Microsoft.VisualStudio.Copilot.UI.Core.dll`, etc.)  
> File location: `%LOCALAPPDATA%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v1.json`

---

## Confirmed Schema

### Provider-level keys (top of each object in the array)

| Key | Type | Found in DLL? | Description |
|---|---|---|---|
| `Name` | `string` | ✅ | Provider identifier shown in VS UI (`"Ollama"`, `"OpenAI"`, etc.) |
| `IsApiKeyAvailable` | `bool` | ✅ | Whether an API key is configured for this provider |
| `Models` | `array` | ✅ | Array of model configuration objects |
| `Endpoint` | `int` | ✅ | Protocol type: `10` = Ollama-compatible, `1` = OpenAI-compatible |

### Model-level keys (inside `Models[]`)

| Key | Type | Found in DLL? | Description |
|---|---|---|---|
| `ProviderName` | `string` | ✅ | Must match the parent provider `Name` |
| `isCustom` | `bool` | ✅ | `true` for custom/self-hosted models |
| `isSelected` | `bool` | ✅ | Whether this model is the active selection |
| `customURL` | `string` | ✅ | Local endpoint URL (e.g., `"http://localhost:5000"`) |
| `modelId` | `string` | ✅ | Model identifier matching proxy output |
| `displayName` | `string` | ✅ | Friendly name shown in VS model picker |
| `isToolCallingEnabled` | `bool` | ✅ | Whether model supports function/tool calling |
| `isVisionEnabled` | `bool` | ✅ | Whether model supports image inputs |
| `maxInputTokens` | `int` | ✅ | Context window size |
| `maxOutputTokens` | `int` | ✅ | Max generation tokens |
| `maxTokens` | `int` | ✅ | Alternative general token limit |
| `tokenLimit` | `int` | ✅ | Another token limit variant |
| `endpointUrl` | `string` | ✅ | Alternative URL field (camelCase) |
| `capabilities` | `object` | ✅ | Capabilities flags object |
| `ModelFamily` | `string` | ✅ | Model family grouping |
| **`UseThinking`** | **`bool`** | ✅ | **Enables/disables thinking (NOT `IsThinkingEnabled`)** |
| **`ThinkingBudget`** | **`int`** | ✅ | **Thinking token budget** |

---

## Key Findings

### ❌ `IsThinkingEnabled` does NOT exist
It was invented — the actual keys from the Copilot DLLs are:
- **`UseThinking`** (`bool`) — enables/disables thinking/reasoning
- **`ThinkingBudget`** (`int`) — the thinking token budget

Also confirmed via DLL getters/setters:
- `get_ThinkingBudget` / `set_ThinkingBudget`
- `get_UseThinking` / `set_UseThinking`
- `get_MinimumThinkingBudget` / `get_MaximumThinkingBudget`
- `ThinkingBudgetAsync` (async variant)

### ❌ Only ONE model should have `isSelected: true`
Marking both as selected is ambiguous for VS.

### ℹ️ Inconsistent casing
VS Copilot mixes PascalCase and camelCase:
- Top-level/provider keys: **PascalCase** (`Name`, `Models`, `Endpoint`, `ProviderName`)
- Model-level keys: mostly **camelCase** (`isCustom`, `displayName`, `maxInputTokens`, `isToolCallingEnabled`)
- Exception: `UseThinking`, `ThinkingBudget`, `ModelFamily` use PascalCase

---

## Understanding `ThinkingBudget`

### What it is

`ThinkingBudget` is the maximum number of tokens the model is allowed to spend on its internal **chain-of-thought reasoning** before producing the final visible response. DeepSeek V4 Pro and V4 Flash are reasoning models — they "think" through problems internally before answering.

### How it works on the DeepSeek side

DeepSeek reasoning models operate in two phases:

1. **Thinking phase** — The model internally reasons through the problem, producing a chain-of-thought. These tokens are stored as `reasoning_content` in the API response. They consume context window space and are billed, but are **not shown** to the user by default.

2. **Answer phase** — After the thinking budget is exhausted (or the model finishes reasoning), it produces the final `content` visible to the user.

```
┌─────────────────────────────────────────────────────┐
│  DeepSeek Reasoning Flow                             │
│                                                      │
│  User: "Explain async/await in C#"                    │
│                                                      │
│  ┌── Thinking (up to ThinkingBudget tokens) ──┐     │
│  │ "I need to cover:                            │     │
│  │  1. Task vs ValueTask                       │     │
│  │  2. State machine generation                │     │
│  │  3. ConfigureAwait(false)                   │     │
│  │  4. Exception handling in async             │     │
│  │  ..."                                        │     │
│  └──────────────────────────────────────────────┘     │
│                      ↓                                │
│  ┌── Final Answer ─────────────────────────────┐     │
│  │ "Async/await in C# is a language feature     │     │
│  │  that allows non-blocking... "               │     │
│  └──────────────────────────────────────────────┘     │
└─────────────────────────────────────────────────────┘
```

### DeepSeek API format

The `ThinkingBudget` config value is relayed to DeepSeek via a `thinking` object in chat completion requests:

```json
{
  "model": "deepseek-v4-pro",
  "messages": [...],
  "thinking": {
    "type": "enabled",
    "budget_tokens": 32000
  }
}
```

| Parameter | Effect |
|---|---|
| `type: "enabled"` | Turns on reasoning/thinking |
| `type: "disabled"` | No thinking, direct answer only |
| `budget_tokens` | Max tokens for the thinking phase |

### Recommended sizes

| Budget | Use case | Trade-off |
|---|---|---|
| **0** (or `UseThinking: false`) | Trivial completions, simple autocomplete | Fastest, cheapest, but less thorough |
| **4096** | Simple coding tasks (one-liners, boilerplate) | Light reasoning, minimal overhead |
| **16000** | Moderate development work (refactoring, debugging) | Good balance for most scenarios |
| **32000** | Complex architecture/design decisions | Deep reasoning, ideal for Copilot chat |
| **64000+** | Maximum depth analysis | Leaves less room for the answer within `maxOutputTokens` |

> ⚠️ **Important:** Thinking tokens count toward `maxOutputTokens`. If `ThinkingBudget` is too large relative to `maxOutputTokens`, the model may not have enough space left for the actual answer. A safe ratio is `ThinkingBudget` ≤ 25% of `maxOutputTokens`.

### How the proxy handles thinking

The proxy already manages reasoning across multi-turn conversations:

- **Captures** thinking content from DeepSeek responses (`reasoning_content` field)
- **Re-injects** cached reasoning into subsequent messages via `ModifyRequest()`
- This ensures the model "remembers" its earlier reasoning across conversation turns

However, the proxy currently does **not** forward a `thinking`/`budget_tokens` parameter to DeepSeek — it only passes `model`, `messages`, `stream`, `max_tokens`, and `tools`. If VS Copilot sends `UseThinking`/`ThinkingBudget` in its request, the proxy would need to be updated to relay them as a `thinking` object.

---

## Recommended Configuration

### Project setup — `appsettings.json`

```json
{
  "DeepSeek": {
	"ApiKey": "sk-YOUR-DEEPSEEK-API-KEY-HERE",
	"BaseUrl": "https://api.deepseek.com",
	"ModelPro": "deepseek-v4-pro",
	"PortPro": 5000,
	"ModelFlash": "deepseek-v4-flash",
	"PortFlash": 5001
  }
}
```

### VS Copilot config — `ConfiguredBringYourOwnModel_v1.json`

```json
[
  {
	"Name": "Ollama",
	"IsApiKeyAvailable": true,
	"Models": [
	  {
		"ProviderName": "Ollama",
		"isCustom": true,
		"isSelected": true,
		"customURL": "http://localhost:5000",
		"modelId": "deepseek-v4-pro",
		"displayName": "DeepSeek V4 Pro",
		"isToolCallingEnabled": true,
		"isVisionEnabled": false,
		"maxInputTokens": 840000,
		"maxOutputTokens": 128000,
		"UseThinking": true,
		"ThinkingBudget": 32000
	  },
	  {
		"ProviderName": "Ollama",
		"isCustom": true,
		"isSelected": false,
		"customURL": "http://localhost:5001",
		"modelId": "deepseek-v4-flash",
		"displayName": "DeepSeek V4 Flash",
		"isToolCallingEnabled": true,
		"isVisionEnabled": false,
		"maxInputTokens": 840000,
		"maxOutputTokens": 128000,
		"UseThinking": true,
		"ThinkingBudget": 32000
	  }
	],
	"Endpoint": 10
  }
]
```

---

## How to Verify

The safest way to confirm the exact schema for your VS version:

1. Open **Tools > Options > GitHub > Copilot > Bring Your Own Model** in VS 2026
2. Add one model through the UI
3. Then inspect the file VS writes to:
   ```
   %LOCALAPPDATA%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v1.json
   ```
4. Copy the exact casing and key names it generates

---

## UI Field Mapping (from DLL string resources)

| UI Label (String Resource) | JSON Key |
|---|---|
| `BringYourOwnKeyModelIdString` | `modelId` |
| `BringYourOwnKeyDisplayNameString` | `displayName` |
| `BringYourOwnKeyEndpointUrlString` | `customURL` |
| `BringYourOwnKeyTokenLimitString` | `maxTokens` / `tokenLimit` |
| `BringYourOwnKeyMaxInputTokensToolTip` | `maxInputTokens` |
| `BringYourOwnKeyMaxOutputTokensToolTip` | `maxOutputTokens` |
| `BringYourOwnKeySupportsToolCallingString` | `isToolCallingEnabled` |
| `BringYourOwnKeySupportsImageContextString` | `isVisionEnabled` |

---

## Related Classes Found in Copilot DLLs

| Class | Purpose |
|---|---|
| `CopilotModel` | Core model configuration class |
| `CopilotModelCapabilities` | Model capabilities flags |
| `CopilotModelThinkingOptions` | Thinking/reasoning options |
| `CopilotModelEndpoint` | Endpoint type enum |
| `CopilotModelBillingType` | Billing/pricing category |
| `CopilotModelPurpose` | Model purpose enum |
| `ModelProviderConnectionInfo` | Provider connection details |
| `ModelProviderType` | Provider type enum |
