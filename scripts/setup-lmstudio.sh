#!/usr/bin/env bash
set -euo pipefail

# setup-lmstudio.sh
# Configure LM Studio headless daemon (llmster) as a systemd service.
# Based on: https://lmstudio.ai/docs/developer/core/headless_llmster

DEFAULT_MODEL="qwen3.6-35b-a3b@q8_0"
SERVICE_PATH="/etc/systemd/system/lmstudio.service"
LMS_WRAPPER_PATH="/usr/local/bin/lms"

print_usage() {
  cat <<'USAGE'
Usage:
  setup-lmstudio.sh [--user <linux_user>] [--model <model_id>] [--preload-model] [--skip-model-load] [--no-local-embedding-autoload]

Options:
  --user <linux_user>   Linux user that should run LM Studio service.
                        Defaults to SUDO_USER when run with sudo, otherwise current user.
  --model <model_id>    Model identifier to download/load when --preload-model is used
                        (default: qwen3.6-35b-a3b@q8_0).
  --preload-model       Download and load a local model before starting the server.
                        Omit this for LM Link relay mode.
  --skip-model-load     Compatibility alias for the default LM Link relay mode.
  --no-local-embedding-autoload
                        Do not install the conditional local embedding preload hook.
                        By default, LM Studio startup loads the configured embedding
                        model only when /etc/pizzawave/pizzad.json has embeddings
                        enabled, executionMode=local, and a localhost:1234 endpoint.
  -h, --help            Show this help.

Examples:
  sudo ./scripts/setup-lmstudio.sh
  sudo ./scripts/setup-lmstudio.sh --user alice
  sudo ./scripts/setup-lmstudio.sh --user alice --preload-model --model qwen3.6-35b-a3b@q8_0
  sudo ./scripts/setup-lmstudio.sh --skip-model-load
USAGE
}

require_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Error: required command not found: $1" >&2
    exit 1
  fi
}

TARGET_USER="${SUDO_USER:-${USER:-}}"
MODEL_ID="$DEFAULT_MODEL"
PRELOAD_MODEL="false"
LOCAL_EMBEDDING_AUTOLOAD="true"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --user)
      [[ $# -ge 2 ]] || { echo "Error: --user requires a value" >&2; exit 1; }
      TARGET_USER="$2"
      shift 2
      ;;
    --model)
      [[ $# -ge 2 ]] || { echo "Error: --model requires a value" >&2; exit 1; }
      MODEL_ID="$2"
      shift 2
      ;;
    --preload-model)
      PRELOAD_MODEL="true"
      shift
      ;;
    --skip-model-load)
      PRELOAD_MODEL="false"
      shift
      ;;
    --no-local-embedding-autoload)
      LOCAL_EMBEDDING_AUTOLOAD="false"
      shift
      ;;
    -h|--help)
      print_usage
      exit 0
      ;;
    *)
      echo "Error: unknown argument: $1" >&2
      print_usage
      exit 1
      ;;
  esac
done

if [[ -z "$TARGET_USER" ]]; then
  echo "Error: could not determine target user. Pass --user <linux_user>." >&2
  exit 1
fi

if ! id "$TARGET_USER" >/dev/null 2>&1; then
  echo "Error: user '$TARGET_USER' does not exist on this system." >&2
  exit 1
fi

if [[ "$EUID" -ne 0 ]]; then
  echo "Error: run this script as root (sudo) to write systemd unit files." >&2
  exit 1
fi

require_cmd curl
require_cmd systemctl

TARGET_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
if [[ -z "$TARGET_HOME" ]]; then
  echo "Error: could not resolve home directory for '$TARGET_USER'." >&2
  exit 1
fi

LMS_BIN="$TARGET_HOME/.lmstudio/bin/lms"

echo "==> Installing LM Studio daemon/CLI for user '$TARGET_USER'"
sudo -u "$TARGET_USER" -H bash -lc 'curl -fsSL https://lmstudio.ai/install.sh | bash'

if [[ ! -x "$LMS_BIN" ]]; then
  echo "Error: lms binary not found at '$LMS_BIN' after installation." >&2
  exit 1
fi

echo "==> Verifying lms installation"
sudo -u "$TARGET_USER" -H "$LMS_BIN" --help >/dev/null

echo "==> Installing lms command wrapper: $LMS_WRAPPER_PATH"
cat > "$LMS_WRAPPER_PATH" <<EOF
#!/usr/bin/env bash
exec "$LMS_BIN" "\$@"
EOF
chmod 0755 "$LMS_WRAPPER_PATH"

if [[ "$LOCAL_EMBEDDING_AUTOLOAD" == "true" ]]; then
  echo "==> Installing conditional local embedding preload helper"
  cat > /usr/local/bin/pizzawave-load-local-embedding-model <<EOF
#!/usr/bin/env python3
import json
import subprocess
import sys
import time
from urllib.parse import urlparse
from urllib.request import urlopen

CONFIG = '/etc/pizzawave/pizzad.json'
LMS = '$LMS_BIN'

try:
    with open(CONFIG) as f:
        config = json.load(f)
except Exception as exc:
    print(f'Cannot read {CONFIG}: {exc}', file=sys.stderr)
    sys.exit(0)

emb = config.get('embeddings') or {}
if not emb.get('enabled', False):
    print('Embeddings disabled; not loading local embedding model.')
    sys.exit(0)

mode = (emb.get('executionMode') or 'local').strip().lower()
if mode != 'local':
    print(f'Embedding executionMode={mode}; not loading local embedding model.')
    sys.exit(0)

base_url = (emb.get('openAiBaseUrl') or '').strip()
model = (emb.get('openAiModel') or '').strip()
if not base_url or not model:
    print('Embedding endpoint/model not configured; not loading local embedding model.')
    sys.exit(0)

parsed = urlparse(base_url)
host = (parsed.hostname or '').lower()
port = parsed.port or (443 if parsed.scheme == 'https' else 80)
if host not in {'localhost', '127.0.0.1', '::1'} or port != 1234:
    print(f'Embedding endpoint {base_url} is not local LM Studio; not loading local embedding model.')
    sys.exit(0)

for _ in range(20):
    try:
        with urlopen('http://127.0.0.1:1234/v1/models', timeout=1):
            break
    except Exception:
        time.sleep(1)
else:
    print('LM Studio endpoint did not become ready; not loading embedding model.', file=sys.stderr)
    sys.exit(1)

print(f'Loading local embedding model {model}')
subprocess.run([LMS, 'load', model, '--identifier', model, '--yes'], check=True)
EOF
  chmod 0755 /usr/local/bin/pizzawave-load-local-embedding-model
fi

echo "==> Installing LM Studio chat completion health helper"
cat > /usr/local/bin/pizzawave-check-lmstudio-chat-model <<'EOF'
#!/usr/bin/env python3
import json
import sys
from urllib.request import Request, urlopen

model = sys.argv[1] if len(sys.argv) > 1 else ""
if not model:
    print("Model identifier is required.", file=sys.stderr)
    sys.exit(2)

payload = json.dumps({
    "model": model,
    "messages": [{"role": "user", "content": "Reply with OK only."}],
    "temperature": 0,
    "max_tokens": 4,
}).encode("utf-8")
request = Request(
    "http://127.0.0.1:1234/v1/chat/completions",
    data=payload,
    headers={"Content-Type": "application/json"},
    method="POST",
)
try:
    with urlopen(request, timeout=45) as response:
        body = json.loads(response.read().decode("utf-8"))
except Exception as exc:
    print(f"Chat completion probe failed: {exc}", file=sys.stderr)
    sys.exit(1)

choice = (body.get("choices") or [{}])[0]
content = (((choice.get("message") or {}).get("content")) or "").strip()
usage = body.get("usage") or {}
completion_tokens = int(usage.get("completion_tokens") or 0)
total_tokens = int(usage.get("total_tokens") or 0)
if not content or (completion_tokens <= 0 and total_tokens <= 0):
    print(
        "Chat completion probe returned no valid result "
        f"(content_length={len(content)}, completion_tokens={completion_tokens}, total_tokens={total_tokens}).",
        file=sys.stderr,
    )
    sys.exit(1)

print(f"Chat completion probe succeeded for {model}: {content[:80]}")
EOF
chmod 0755 /usr/local/bin/pizzawave-check-lmstudio-chat-model

if [[ "$PRELOAD_MODEL" == "true" ]]; then
  echo "==> Downloading model: $MODEL_ID"
  sudo -u "$TARGET_USER" -H "$LMS_BIN" get "$MODEL_ID" --yes
fi

echo "==> Writing systemd unit: $SERVICE_PATH"
{
  echo "[Unit]"
  echo "Description=LM Studio Server"
  echo "After=network-online.target"
  echo "Wants=network-online.target"
  echo ""
  echo "[Service]"
  echo "Type=oneshot"
  echo "RemainAfterExit=yes"
  echo "User=$TARGET_USER"
  echo "Environment=\"HOME=$TARGET_HOME\""
  echo "ExecStartPre=$LMS_BIN daemon up"
  if [[ "$PRELOAD_MODEL" == "true" ]]; then
    echo "ExecStartPre=$LMS_BIN load $MODEL_ID --yes"
  fi
  echo "ExecStart=$LMS_BIN server start --bind 127.0.0.1 --port 1234"
  if [[ "$PRELOAD_MODEL" == "true" ]]; then
    echo "ExecStartPost=/usr/local/bin/pizzawave-check-lmstudio-chat-model $MODEL_ID"
    echo "Restart=on-failure"
    echo "RestartSec=20s"
  fi
  if [[ "$LOCAL_EMBEDDING_AUTOLOAD" == "true" ]]; then
    echo "ExecStartPost=/usr/local/bin/pizzawave-load-local-embedding-model"
  fi
  echo "ExecStop=$LMS_BIN daemon down"
  echo ""
  echo "[Install]"
  echo "WantedBy=multi-user.target"
} > "$SERVICE_PATH"

echo "==> Reloading systemd and enabling service"
systemctl daemon-reload
systemctl enable lmstudio.service

echo "==> Starting service"
systemctl restart lmstudio.service

echo "==> Verifying service"
systemctl --no-pager --full status lmstudio.service || true

echo "==> Testing API endpoint"
if curl -fsS http://localhost:1234/v1/models >/dev/null; then
  echo "LM Studio service is up and API is reachable at http://localhost:1234"
else
  echo "Warning: API check failed. Inspect logs with: journalctl -u lmstudio -n 100 --no-pager" >&2
  exit 1
fi

if [[ "$PRELOAD_MODEL" == "true" ]]; then
  echo "==> Testing chat completion endpoint"
  if /usr/local/bin/pizzawave-check-lmstudio-chat-model "$MODEL_ID"; then
    echo "LM Studio chat completion endpoint is producing valid completions."
  else
    echo "Warning: chat completion check failed. Inspect logs with: journalctl -u lmstudio -n 100 --no-pager" >&2
    exit 1
  fi
fi
