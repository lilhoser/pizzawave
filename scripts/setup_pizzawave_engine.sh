#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT="/opt/pizzawave/pizzad"
SERVICE_USER="pizzawave"
CONFIG_DIR="/etc/pizzawave"
DATA_DIR="/var/lib/pizzawave"
SERVICE_PATH="/etc/systemd/system/pizzad.service"
PUBLISH_DIR=""

usage() {
  cat <<'USAGE'
Usage:
  setup_pizzawave_engine.sh --publish-dir <path>

Installs PizzaWave Engine (pizzad) as a systemd service.
Build/publish first, for example:
  dotnet publish ./pizzad/pizzad.csproj -c Release -o ./artifacts/pizzad
  sudo ./scripts/setup_pizzawave_engine.sh --publish-dir ./artifacts/pizzad
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --publish-dir)
      PUBLISH_DIR="$2"
      shift 2
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

mkdir -p "$INSTALL_ROOT" "$CONFIG_DIR" "$DATA_DIR/audio" "$DATA_DIR/import-cache"
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
    "importCacheRoot": "/var/lib/pizzawave/import-cache"
  },
  "ingest": { "callstreamBind": "127.0.0.1", "callstreamPort": 9123 },
  "transcription": { "provider": "none", "analogSampleRate": 8000 },
  "trunkRecorder": {
    "configPath": "/etc/trunk-recorder/config.json",
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
chmod 0600 "$CONFIG_DIR/pizzad.token"

cat > "$SERVICE_PATH" <<EOF
[Unit]
Description=PizzaWave Engine
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$INSTALL_ROOT
ExecStart=$INSTALL_ROOT/pizzad --config $CONFIG_DIR/pizzad.json
Restart=on-failure
RestartSec=5
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable pizzad.service
systemctl restart pizzad.service

echo "PizzaWave Engine installed."
echo "Web UI: http://$(hostname -I | awk '{print $1}'):8080"
echo "Token file: $CONFIG_DIR/pizzad.token"
