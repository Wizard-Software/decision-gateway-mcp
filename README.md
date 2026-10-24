# Decision-Gateway MCP

An [MCP](https://modelcontextprotocol.io) server that exposes a single tool, `evaluate_decision`, for fast classification decisions. It puts two decision engines behind one interface:

- **Jev** — fast, low-cost, text-only decisions.
- **OpenAI Decisions** — multimodal decisions (text and images).

The calling model picks one of the allowed options for a query. The gateway validates the request before any HTTP call, routes it to the right provider, and returns a uniform result.

> **Status:** design complete, implementation not started. The first milestone is a spike that confirms the official C# MCP SDK works with Native AOT. If it doesn't, the plan B is Go.

## Why

A model that needs a quick yes/no or one-of-N decision shouldn't pay for a full reasoning call every time. With Decision-Gateway it can send cheap text decisions to Jev and switch to OpenAI when the context includes an image. When a call is invalid (for example, an image sent to Jev), the model gets an explicit validation error and can correct the call itself.

## How it works

1. **Startup.** The server checks which API keys are set and builds the tool schema from them. Only configured providers appear in the `provider` enum, and `image_payload` is hidden when only Jev is available. With no keys at all, the server exits with a configuration error (fail-fast).
2. **Validation.** Every request is checked before any HTTP call: query and option lengths, unique options, image format and size, and provider/image compatibility.
3. **Routing.** An explicit `provider` is always respected. Without one, the configured default is used, and requests with an image go to OpenAI.
4. **Fallback.** If the default provider fails with a transient error (network, timeout, 5xx, 429), the gateway can retry once on the other provider. The response always says which provider answered (`provider`, `fallback_used`). An explicit `provider` disables fallback, image requests never fall back to Jev, and fallback can be turned off in configuration.

## Tool contract

### Input

| Field | Type | Required | Notes |
|---|---|---|---|
| `query` | string | yes | Decision context or question, 1–16 384 characters. |
| `options` | string[] | yes | 2–50 unique, non-empty options, each up to 512 characters (16 384 in total). |
| `provider` | `"jev"` \| `"openai"` | no | Only configured providers are listed. Without it, the default provider is used (OpenAI when an image is present). |
| `image_payload` | string (base64) | no | PNG, JPEG, WEBP or GIF, up to 20 MiB decoded. OpenAI only. |

### Success

```json
{
  "selected_option": "refund",
  "confidence": 0.87,
  "latency_ms": 142,
  "provider": "jev",
  "fallback_used": false
}
```

`confidence` (0.0–1.0) is the raw value returned by the provider. Values from different providers are **not** calibrated against each other, so don't compare them directly.

### Errors

Errors are returned as tool results with `isError: true`, so the model can read the message and fix its call:

| Code | When |
|---|---|
| `VALIDATION_ERROR` | Invalid `query`, `options` or `image_payload`. |
| `IMAGE_NOT_SUPPORTED` | An image was sent to Jev. |
| `PROVIDER_NOT_CONFIGURED` | The requested provider has no API key. |
| `PROVIDER_REJECTED` | The provider rejected the request (401, 403, other 4xx except 429). |
| `PROVIDER_UNAVAILABLE` | Network error, timeout, 5xx or 429 with no fallback available. |
| `PROVIDER_INVALID_RESPONSE` | The provider's response doesn't match the contract. |
| `OVERLOADED` | Too many concurrent image requests are waiting. |
| `INTERNAL_ERROR` | Unexpected server error. |

Error messages never include API keys or request headers.

## Configuration

API keys and endpoints come **only from environment variables**. Other settings can also be placed in an optional `appsettings.json` next to the executable.

| Environment variable | Default | Description |
|---|---|---|
| `JEV_API_KEY` | — | Jev API key. |
| `OPENAI_API_KEY` | — | OpenAI API key. |
| `DecisionGateway__DefaultProvider` | `jev` with two keys | Default provider. |
| `DecisionGateway__Fallback__Enabled` | `true` | Enables automatic fallback. |
| `DecisionGateway__Providers__Jev__TimeoutMs` | `2000` | Jev HTTP timeout (1–60 000 ms). |
| `DecisionGateway__Providers__OpenAI__TimeoutMs` | `5000` | OpenAI HTTP timeout (1–60 000 ms). |
| `Logging__LogLevel__Default` | `Information` | Log level (logs go to stderr). |

At least one API key is required. Settings are read once at startup, so restart the server after changing them.

## Usage with an MCP client

Example registration for Claude Code or Claude Desktop:

```json
{
  "mcpServers": {
    "decision-gateway": {
      "command": "/path/to/decision-gateway-mcp",
      "env": {
        "JEV_API_KEY": "<your Jev key>",
        "OPENAI_API_KEY": "<your OpenAI key>"
      }
    }
  }
}
```

## Distribution (planned)

- **Native executables** for Windows (x64), Linux (x64, arm64) and macOS (arm64, x64) on GitHub Releases. No .NET runtime is required. Each release ships with `SHA256SUMS`, build provenance attestation and an SBOM. From 1.0.0, binaries are signed (Developer ID with notarization on macOS, Authenticode on Windows).
- **NuGet package** runnable with `dnx` (requires the .NET 10 SDK) and listed in the MCP Registry.

## Technology

- C# / .NET with Native AOT, using the official C# MCP SDK over the stdio transport.
- Three projects: **Core** (validation, routing, fallback policy), **Providers** (Jev and OpenAI HTTP clients) and **Host** (MCP server and configuration).
- Performance targets: gateway overhead under 10 ms p95 and process startup under 100 ms.
- CI on GitHub Actions with a Windows/Linux/macOS build matrix.

## License

License to be determined.
