#!/usr/bin/env bash
set -euo pipefail

# setup-lmstudio.sh
# Configure LM Studio headless daemon (llmster) as a systemd service.
# Based on: https://lmstudio.ai/docs/developer/core/headless_llmster

DEFAULT_MODEL="qwen3.6-35b-a3b@q8_0"
SERVICE_PATH="/etc/systemd/system/lmstudio.service"

print_usage() {
  cat <<'USAGE'
Usage:
  setup-lmstudio.sh [--user <linux_user>] [--model <model_id>] [--preload-model] [--skip-model-load]

Options:
  --user <linux_user>   Linux user that should run LM Studio service.
                        Defaults to SUDO_USER when run with sudo, otherwise current user.
  --model <model_id>    Model identifier to download/load when --preload-model is used
                        (default: qwen3.6-35b-a3b@q8_0).
  --preload-model       Download and load a local model before starting the server.
                        Omit this for LM Link relay mode.
  --skip-model-load     Compatibility alias for the default LM Link relay mode.
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
