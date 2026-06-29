# LM Studio AI Routing

PizzaWave treats chat/AI Insights and embeddings as separate pipelines. They can
use different OpenAI-compatible endpoints.

## Recommended Split

For a client rig that should not run a large LLM locally:

- AI Insights: remote LM Studio or LM Link endpoint on the GPU host.
- Embeddings: local LM Studio endpoint on the rig.
- Qdrant: local native `qdrant.service` on the rig.

Example:

```json
{
  "aiInsights": {
    "enabled": true,
    "executionMode": "remote",
    "openAiBaseUrl": "http://paxan:1234/v1",
    "openAiModel": "qwen/qwen3.6-35b-a3b@q8_0"
  },
  "embeddings": {
    "enabled": true,
    "executionMode": "local",
    "openAiBaseUrl": "http://localhost:1234/v1",
    "openAiModel": "text-embedding-nomic-embed-text-v1.5"
  }
}
```

The base URL decides which server receives the request. `executionMode` records
operator intent and is used by setup helpers and validation copy; it does not
rewrite endpoints.

## Avoid Ambiguous Model Resolution

On rigs that should not host chat models:

- disable LM Studio peer discovery/model sharing for that rig;
- disable JIT/local model loading;
- remove large local LLM files if they are not intentionally used;
- keep only the small local embedding model if embeddings are local.

Use explicit LM Studio model IDs on the GPU host, such as:

```text
qwen/qwen3.6-35b-a3b@q8_0
```

The settings UI prefers LM Studio's native `/api/v1/models` endpoint so the
operator sees loadable model IDs instead of loaded runtime aliases. Runtime
aliases such as `paxan-qwen3.6-35b-a3b@q8_0` may appear in OpenAI-compatible
`/v1/models`, but they are not loadable through LM Studio's REST model-load
endpoint and should not be used as the saved PizzaWave model setting.

## Qwen Thinking Mode

PizzaWave expects structured answers in `message.content`. Incident extraction
and evidence verification should not receive final JSON in `reasoning_content`.

For LM Studio with some Unsloth Qwen GGUFs, request-side
`chat_template_kwargs.enable_thinking=false` may not be honored. Add this as the
first line of the model's Template Jinja and reload the model:

```jinja
{%- set enable_thinking = false %}
```

Before using the model in production, test:

- `finish_reason` is `stop`;
- `message.content` contains the final JSON;
- `reasoning_content` is empty or missing;
- `reasoning_tokens` is `0`.

## Local Embedding Autoload

If LM Studio JIT loading is disabled, the local embedding model must be loaded
explicitly. `setup-lmstudio.sh` installs
`/usr/local/bin/pizzawave-load-local-embedding-model` and runs it as
`ExecStartPost` for `lmstudio.service`.

The helper loads a model only when all are true:

- `/etc/pizzawave/pizzad.json` has embeddings enabled;
- `embeddings.executionMode` is `local`;
- `embeddings.openAiBaseUrl` points at localhost:1234;
- `embeddings.openAiModel` is configured.

Remote embedding endpoints are never preloaded by the local helper.
