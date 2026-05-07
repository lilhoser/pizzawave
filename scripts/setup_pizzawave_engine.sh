#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT="/opt/pizzawave/pizzad"
SERVICE_USER="pizzawave"
CONFIG_DIR="/etc/pizzawave"
DATA_DIR="/var/lib/pizzawave"
SERVICE_PATH="/etc/systemd/system/pizzad.service"
PUBLISH_DIR=""
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_LMSTUDIO="false"
LMSTUDIO_USER="${SUDO_USER:-${USER:-}}"
LMSTUDIO_MODEL="qwen3.6-35b-a3b@q8_0"
LMSTUDIO_SKIP_MODEL_LOAD="true"

usage() {
  cat <<'USAGE'
Usage:
  setup_pizzawave_engine.sh --publish-dir <path> [--with-lmstudio] [--lmstudio-user <user>] [--lmstudio-model <model>] [--preload-lmstudio-model]

Installs PizzaWave Engine (pizzad) as a systemd service.
Build/publish first, for example:
  dotnet publish ./pizzad/pizzad.csproj -c Release -o ./artifacts/pizzad
  sudo ./scripts/setup_pizzawave_engine.sh --publish-dir ./artifacts/pizzad

LM Studio is optional and is used by aiInsights/summarization only. Local Linux
transcription remains controlled by /etc/pizzawave/pizzad.json.
By default LM Studio is installed in LM Link relay mode and no local LLM is
downloaded or preloaded.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --publish-dir)
      PUBLISH_DIR="$2"
      shift 2
      ;;
    --with-lmstudio)
      INSTALL_LMSTUDIO="true"
      shift
      ;;
    --lmstudio-user)
      LMSTUDIO_USER="$2"
      shift 2
      ;;
    --lmstudio-model)
      LMSTUDIO_MODEL="$2"
      shift 2
      ;;
    --preload-lmstudio-model)
      LMSTUDIO_SKIP_MODEL_LOAD="false"
      shift
      ;;
    --skip-lmstudio-model-load)
      LMSTUDIO_SKIP_MODEL_LOAD="true"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac
done

if [[ "$EUID" -ne 0 ]]; then
  echo "Run with sudo/root." >&2
  exit 1
fi

if [[ -z "$PUBLISH_DIR" || ! -d "$PUBLISH_DIR" ]]; then
  echo "--publish-dir is required and must exist." >&2
  exit 1
fi

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  adduser --system --group --home "$DATA_DIR" "$SERVICE_USER"
fi

mkdir -p "$INSTALL_ROOT" "$CONFIG_DIR" "$DATA_DIR/audio" "$DATA_DIR/import-cache" "$DATA_DIR/appdata"
find "$INSTALL_ROOT" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp -a "$PUBLISH_DIR"/. "$INSTALL_ROOT"/

if [[ ! -f "$CONFIG_DIR/pizzad.json" ]]; then
  cat > "$CONFIG_DIR/pizzad.json" <<'JSON'
{
  "server": { "httpBind": "0.0.0.0", "httpPort": 8080 },
  "auth": {
    "mode": "token",
    "readRequiresAuth": false,
    "writeRequiresAuth": true,
    "tokenFile": "/etc/pizzawave/pizzad.token"
  },
  "storage": {
    "databasePath": "/var/lib/pizzawave/pizzad.db",
    "audioRoot": "/var/lib/pizzawave/audio",
    "importCacheRoot": "/var/lib/pizzawave/import-cache",
    "appDataRoot": "/var/lib/pizzawave/appdata"
  },
  "ingest": { "callstreamBind": "127.0.0.1", "callstreamPort": 9123 },
  "transcription": { "provider": "none", "analogSampleRate": 8000 },
  "aiInsights": {
    "enabled": false,
    "openAiBaseUrl": "http://localhost:1234/v1",
    "openAiApiKey": "",
    "openAiModel": "",
    "batchSize": 20,
    "maxPendingCalls": 1000,
    "timeoutMs": 600000,
    "maxRetries": 2
  },
  "trunkRecorder": {
    "configPath": "/etc/trunk-recorder/config.json",
    "talkgroupsPath": "/etc/trunk-recorder/talkgroups.csv",
    "logServiceName": "trunk-recorder",
    "healthWindowMinutes": 5
  }
}
JSON
fi

if [[ ! -f "$CONFIG_DIR/pizzad.token" ]]; then
  umask 077
  python3 - <<'PY' > "$CONFIG_DIR/pizzad.token"
import secrets
print(secrets.token_hex(32))
PY
fi

chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR"
chown root:"$SERVICE_USER" "$CONFIG_DIR/pizzad.json" "$CONFIG_DIR/pizzad.token"
chmod 0640 "$CONFIG_DIR/pizzad.json"
chmod 0640 "$CONFIG_DIR/pizzad.token"
if getent group trunk-recorder >/dev/null 2>&1; then
  usermod -aG trunk-recorder "$SERVICE_USER" || true
fi

cat > "$SERVICE_PATH" <<EOF
[Unit]
Description=PizzaWave Engine
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$DATA_DIR
ExecStart=$INSTALL_ROOT/pizzad --config $CONFIG_DIR/pizzad.json
Restart=on-failure
RestartSec=5
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
Environment=HOME=$DATA_DIR
Environment=XDG_CONFIG_HOME=$DATA_DIR/appdata
Environment=XDG_DATA_HOME=$DATA_DIR/appdata

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable pizzad.service
systemctl restart pizzad.service

if [[ "$INSTALL_LMSTUDIO" == "true" ]]; then
  LMSTUDIO_SCRIPT="$SCRIPT_DIR/setup-lmstudio.sh"
  if [[ ! -x "$LMSTUDIO_SCRIPT" ]]; then
    echo "LM Studio setup script not found or not executable: $LMSTUDIO_SCRIPT" >&2
    exit 1
  fi

  LMSTUDIO_ARGS=(--user "$LMSTUDIO_USER" --model "$LMSTUDIO_MODEL")
  if [[ "$LMSTUDIO_SKIP_MODEL_LOAD" == "true" ]]; then
    LMSTUDIO_ARGS+=(--skip-model-load)
  else
    LMSTUDIO_ARGS+=(--preload-model)
  fi
  "$LMSTUDIO_SCRIPT" "${LMSTUDIO_ARGS[@]}"
fi

echo "PizzaWave Engine installed."
echo "Web UI: http://$(hostname -I | awk '{print $1}'):8080"
echo "Token file: $CONFIG_DIR/pizzad.token"
