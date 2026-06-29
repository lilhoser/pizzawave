#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT="/opt/pizzawave/pizzad"
SCRIPT_ROOT="/opt/pizzawave/scripts"
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
RETROFIT_EXISTING_TR="false"
REMOVE_LEGACY_APPS="false"
REQUIRE_WIZARD="false"
BACKUP_ROOT="/var/backups/pizzawave"

usage() {
  cat <<'USAGE'
Usage:
  setup_pizzawave_engine.sh --publish-dir <path> [--with-lmstudio] [--lmstudio-user <user>] [--lmstudio-model <model>] [--preload-lmstudio-model]
  setup_pizzawave_engine.sh --publish-dir <path> --retrofit-existing-tr --remove-legacy-apps --require-wizard

Installs PizzaWave Engine (pizzad) as a systemd service.
Build/publish first, for example:
  dotnet publish ./pizzad/pizzad.csproj -c Release -r linux-x64 --self-contained true -p:SelfContained=true -p:PublishSingleFile=false -o ./artifacts/pizzad
  sudo ./scripts/setup_pizzawave_engine.sh --publish-dir ./artifacts/pizzad

LM Studio is optional and is used by aiInsights/summarization only. Local Linux
transcription remains controlled by /etc/pizzawave/pizzad.json.
By default LM Studio is installed in LM Link relay mode and no local LLM is
downloaded or preloaded.

RPI retrofit mode preserves the existing trunk-recorder config/talkgroups,
removes retired PizzaWave app/tr-health artifacts, and leaves TR itself installed.
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
    --retrofit-existing-tr)
      RETROFIT_EXISTING_TR="true"
      shift
      ;;
    --remove-legacy-apps)
      REMOVE_LEGACY_APPS="true"
      shift
      ;;
    --require-wizard)
      REQUIRE_WIZARD="true"
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

backup_path() {
  local path="$1"
  local dest_root="$2"
  if [[ -e "$path" ]]; then
    mkdir -p "$dest_root$(dirname "$path")"
    cp -a "$path" "$dest_root$path"
  fi
}

backup_existing_tr() {
  local stamp backup_dir
  stamp="$(date -u +%Y%m%d-%H%M%S)"
  backup_dir="$BACKUP_ROOT/retrofit-$stamp"
  mkdir -p "$backup_dir"
  backup_path /etc/trunk-recorder/config.json "$backup_dir"
  backup_path /etc/trunk-recorder/talkgroups.csv "$backup_dir"
  backup_path /etc/systemd/system/trunk-recorder.service "$backup_dir"
  backup_path /lib/systemd/system/trunk-recorder.service "$backup_dir"
  echo "$backup_dir"
}

remove_legacy_apps() {
  echo "Removing retired PizzaWave app/tr-health artifacts. TR config and TR service are preserved."
  systemctl stop pizzapi.service 2>/dev/null || true
  systemctl disable pizzapi.service 2>/dev/null || true
  systemctl stop tr-health-collector.timer 2>/dev/null || true
  systemctl disable tr-health-collector.timer 2>/dev/null || true
  systemctl stop tr-health-collector.service 2>/dev/null || true
  systemctl disable tr-health-collector.service 2>/dev/null || true
  rm -f /etc/systemd/system/pizzapi.service
  rm -f /etc/systemd/system/tr-health-collector.timer
  rm -f /etc/systemd/system/tr-health-collector.service
  rm -f /etc/xdg/autostart/pizzapi.desktop
  if [[ -n "${SUDO_USER:-}" && -d "/home/${SUDO_USER}/.config/autostart" ]]; then
    rm -f "/home/${SUDO_USER}/.config/autostart/pizzapi.desktop"
  fi
  if command -v dpkg >/dev/null 2>&1 && dpkg -s pizzapi >/dev/null 2>&1; then
    dpkg --purge pizzapi || apt-get remove -y pizzapi || true
  fi
  rm -rf /opt/pizzapi /etc/pizzapi /var/lib/pizzapi
  systemctl daemon-reload
}

if [[ "$RETROFIT_EXISTING_TR" == "true" ]]; then
  if [[ ! -f /etc/trunk-recorder/config.json ]]; then
    echo "--retrofit-existing-tr requires /etc/trunk-recorder/config.json." >&2
    exit 1
  fi
  if [[ ! -f /etc/trunk-recorder/talkgroups.csv ]]; then
    echo "--retrofit-existing-tr requires /etc/trunk-recorder/talkgroups.csv." >&2
    exit 1
  fi
  TR_BACKUP_DIR="$(backup_existing_tr)"
  echo "Existing TR config/talkgroups backed up to: $TR_BACKUP_DIR"
fi

if [[ "$REMOVE_LEGACY_APPS" == "true" ]]; then
  remove_legacy_apps
fi

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  adduser --system --group --home "$DATA_DIR" "$SERVICE_USER"
fi

mkdir -p "$INSTALL_ROOT" "$SCRIPT_ROOT" "$CONFIG_DIR" "$DATA_DIR/audio" "$DATA_DIR/import-cache" "$DATA_DIR/appdata"
find "$INSTALL_ROOT" -mindepth 1 -maxdepth 1 -exec rm -rf {} +
cp -a "$PUBLISH_DIR"/. "$INSTALL_ROOT"/
chmod 0755 "$INSTALL_ROOT/pizzad" || true
for helper in setup_trunk_recorder.sh tr_tune.sh setup-lmstudio.sh setup-faster-whisper.sh pizzawave_setup_admin.sh; do
  if [[ -f "$SCRIPT_DIR/$helper" ]]; then
    cp "$SCRIPT_DIR/$helper" "$SCRIPT_ROOT/$helper"
    perl -pi -e 's/\r$//' "$SCRIPT_ROOT/$helper"
    chmod 0755 "$SCRIPT_ROOT/$helper"
  fi
done

if [[ -f "$SCRIPT_ROOT/pizzawave_setup_admin.sh" ]]; then
  cat > /etc/sudoers.d/pizzawave-setup <<'SUDOERS'
pizzawave ALL=(root) NOPASSWD: /opt/pizzawave/scripts/pizzawave_setup_admin.sh *
pizzawave ALL=(root) NOPASSWD: /bin/systemctl restart pizzad.service, /usr/bin/systemctl restart pizzad.service
SUDOERS
  chmod 0440 /etc/sudoers.d/pizzawave-setup
fi

if [[ ! -f "$CONFIG_DIR/pizzad.json" ]]; then
  cat > "$CONFIG_DIR/pizzad.json" <<'JSON'
{
  "server": { "httpBind": "0.0.0.0", "httpPort": 8080 },
  "branding": { "stackName": "PizzaWave" },
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
    "executionMode": "local",
    "openAiBaseUrl": "http://localhost:1234/v1",
    "openAiApiKey": "",
    "openAiModel": "",
    "batchSize": 20,
    "maxPendingCalls": 1000,
    "timeoutMs": 600000,
    "maxRetries": 2
  },
  "embeddings": {
    "enabled": false,
    "executionMode": "local",
    "openAiBaseUrl": "http://localhost:1234/v1",
    "openAiApiKey": "",
    "openAiModel": "nomic-embed-text",
    "qdrantBaseUrl": "http://localhost:6333",
    "qdrantApiKey": "",
    "qdrantServiceName": "qdrant",
    "qdrantStoragePath": "/var/lib/pizzawave/qdrant",
    "collection": "pizzawave_calls",
    "vectorSize": 768,
    "workers": 1,
    "maxQueueDepthWhenTranscriptionBusy": 25,
    "searchLimit": 40,
    "searchWindowMinutes": 120
  },
  "trunkRecorder": {
    "configPath": "/etc/trunk-recorder/config.json",
    "talkgroupsPath": "/etc/trunk-recorder/talkgroups.csv",
    "logServiceName": "trunk-recorder",
    "healthWindowMinutes": 5
  },
  "locations": {
    "monitoredAreas": []
  },
  "setup": {
    "completed": false,
    "wizardVersion": 1,
    "currentStep": "stack"
  }
}
JSON
fi

if [[ "$RETROFIT_EXISTING_TR" == "true" || "$REQUIRE_WIZARD" == "true" ]]; then
  python3 - "$CONFIG_DIR/pizzad.json" "$RETROFIT_EXISTING_TR" "$REQUIRE_WIZARD" <<'PY'
import json, sys
path, retrofit, require_wizard = sys.argv[1], sys.argv[2] == "true", sys.argv[3] == "true"
with open(path, "r", encoding="utf-8-sig") as f:
    data = json.load(f)
data.setdefault("trunkRecorder", {})
data["trunkRecorder"]["configPath"] = "/etc/trunk-recorder/config.json"
data["trunkRecorder"]["talkgroupsPath"] = "/etc/trunk-recorder/talkgroups.csv"
data["trunkRecorder"].setdefault("logServiceName", "trunk-recorder")
data.setdefault("setup", {})
if retrofit:
    data["setup"]["currentStep"] = "stack"
    data["setup"]["installMode"] = "retrofitExistingTr"
if require_wizard or retrofit:
    data["setup"]["completed"] = False
    data["setup"]["completedAtUtc"] = None
with open(path, "w", encoding="utf-8", newline="\n") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
PY
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
chmod 0660 "$CONFIG_DIR/pizzad.json"
chmod 0640 "$CONFIG_DIR/pizzad.token"
if getent group trunk-recorder >/dev/null 2>&1; then
  usermod -aG trunk-recorder "$SERVICE_USER" || true
  if [[ -d /etc/trunk-recorder ]]; then
    chown root:trunk-recorder /etc/trunk-recorder || true
    chmod 0750 /etc/trunk-recorder || true
    for tr_file in /etc/trunk-recorder/config.json /etc/trunk-recorder/talkgroups.csv; do
      if [[ -f "$tr_file" ]]; then
        chown root:trunk-recorder "$tr_file" || true
        chmod 0640 "$tr_file" || true
      fi
    done
  fi
fi
for sdr_group in plugdev dialout; do
  if getent group "$sdr_group" >/dev/null 2>&1; then
    usermod -aG "$sdr_group" "$SERVICE_USER" || true
  fi
done

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
WEB_URL="http://$(hostname -I | awk '{print $1}'):8080"
echo "Web UI: $WEB_URL"
if grep -q '"completed"[[:space:]]*:[[:space:]]*true' "$CONFIG_DIR/pizzad.json"; then
  echo "Setup status: already completed. Open the web UI to manage PizzaWave."
else
  echo "Setup status: wizard required. Open the web UI and complete first-run setup before live ingest starts."
fi
if [[ -n "${DISPLAY:-}" ]] && command -v xdg-open >/dev/null 2>&1; then
  xdg-open "$WEB_URL" >/dev/null 2>&1 || true
fi
echo "Token file: $CONFIG_DIR/pizzad.token"
