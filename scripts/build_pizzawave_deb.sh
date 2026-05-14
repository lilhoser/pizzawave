#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="0.1.0"
RID="linux-x64"
OUTPUT_DIR="$ROOT_DIR/artifacts/packages"
PUBLISH_DIR_OVERRIDE=""

usage() {
  cat <<'USAGE'
Usage:
  scripts/build_pizzawave_deb.sh [--version VERSION] [--rid linux-x64|linux-arm64] [--output-dir PATH] [--publish-dir PATH]

Builds a self-contained PizzaWave Engine Debian package.

Examples:
  ./scripts/build_pizzawave_deb.sh
  ./scripts/build_pizzawave_deb.sh --rid linux-arm64
  ./scripts/build_pizzawave_deb.sh --version 0.2.0 --rid linux-x64
  ./scripts/build_pizzawave_deb.sh --rid linux-arm64 --publish-dir ./artifacts/pizzad-linux-arm64
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      VERSION="$2"
      shift 2
      ;;
    --rid)
      RID="$2"
      shift 2
      ;;
    --output-dir)
      OUTPUT_DIR="$2"
      shift 2
      ;;
    --publish-dir)
      PUBLISH_DIR_OVERRIDE="$2"
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

case "$RID" in
  linux-x64)
    DEB_ARCH="amd64"
    ;;
  linux-arm64)
    DEB_ARCH="arm64"
    ;;
  *)
    echo "Unsupported RID: $RID. Use linux-x64 or linux-arm64." >&2
    exit 1
    ;;
esac

if [[ -z "$PUBLISH_DIR_OVERRIDE" ]] && ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet SDK is required to build the package." >&2
  exit 1
fi

if ! command -v dpkg-deb >/dev/null 2>&1; then
  echo "dpkg-deb is required. Build on Debian/Ubuntu, WSL, or another system with dpkg-deb." >&2
  exit 1
fi

PUBLISH_DIR="${PUBLISH_DIR_OVERRIDE:-$ROOT_DIR/artifacts/pizzad-$RID}"
PKG_ROOT="$ROOT_DIR/artifacts/debroot-pizzawave-$RID"
PKG_NAME="pizzawave_${VERSION}_${DEB_ARCH}.deb"

if [[ -z "$PUBLISH_DIR_OVERRIDE" ]]; then
  rm -rf "$PUBLISH_DIR"
fi
rm -rf "$PKG_ROOT"
mkdir -p "$PUBLISH_DIR" "$PKG_ROOT" "$OUTPUT_DIR"

if [[ -z "$PUBLISH_DIR_OVERRIDE" ]]; then
  dotnet publish "$ROOT_DIR/pizzad/pizzad.csproj" \
    -c Release \
    -r "$RID" \
    --self-contained true \
    /p:SelfContained=true \
    /p:RuntimeIdentifier="$RID" \
    -o "$PUBLISH_DIR" \
    /p:PublishSingleFile=false
fi

if [[ ! -f "$PUBLISH_DIR/libcoreclr.so" && ! -f "$PUBLISH_DIR/libcoreclr.dylib" && ! -f "$PUBLISH_DIR/coreclr.dll" ]]; then
  echo "Self-contained publish verification failed: .NET runtime files were not found in $PUBLISH_DIR." >&2
  exit 1
fi

install -d "$PKG_ROOT/DEBIAN"
install -d "$PKG_ROOT/opt/pizzawave/pizzad"
install -d "$PKG_ROOT/usr/bin"
install -d "$PKG_ROOT/usr/lib/pizzawave/scripts"
install -d "$PKG_ROOT/lib/systemd/system"
install -d "$PKG_ROOT/etc/pizzawave"
install -d "$PKG_ROOT/var/lib/pizzawave/audio"
install -d "$PKG_ROOT/var/lib/pizzawave/import-cache"
install -d "$PKG_ROOT/var/lib/pizzawave/appdata"

cp -a "$PUBLISH_DIR"/. "$PKG_ROOT/opt/pizzawave/pizzad"/
install -m 0755 "$ROOT_DIR/scripts/pizzawave" "$PKG_ROOT/usr/bin/pizzawave"
install -m 0755 "$ROOT_DIR/scripts/build_pizzawave_deb.sh" "$PKG_ROOT/usr/lib/pizzawave/scripts/build_pizzawave_deb.sh"
install -m 0755 "$ROOT_DIR/scripts/setup_pizzawave_engine.sh" "$PKG_ROOT/usr/lib/pizzawave/scripts/setup_pizzawave_engine.sh"
install -m 0755 "$ROOT_DIR/scripts/setup-lmstudio.sh" "$PKG_ROOT/usr/lib/pizzawave/scripts/setup-lmstudio.sh"
install -m 0755 "$ROOT_DIR/scripts/setup-faster-whisper.sh" "$PKG_ROOT/usr/lib/pizzawave/scripts/setup-faster-whisper.sh"
install -m 0755 "$ROOT_DIR/scripts/pizzawave_setup_admin.sh" "$PKG_ROOT/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh"
install -m 0755 "$ROOT_DIR/scripts/pizzawave_configure_callstream.py" "$PKG_ROOT/usr/lib/pizzawave/scripts/pizzawave_configure_callstream.py"
install -m 0755 "$ROOT_DIR/scripts/setup_trunk_recorder.sh" "$PKG_ROOT/usr/lib/pizzawave/scripts/setup_trunk_recorder.sh"
install -m 0755 "$ROOT_DIR/scripts/tr_tune.sh" "$PKG_ROOT/usr/lib/pizzawave/scripts/tr_tune.sh"
install -m 0755 "$ROOT_DIR/scripts/prime_tr_health.py" "$PKG_ROOT/usr/lib/pizzawave/scripts/prime_tr_health.py"

cat > "$PKG_ROOT/lib/systemd/system/pizzad.service" <<'EOF'
[Unit]
Description=PizzaWave Engine
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=pizzawave
Group=pizzawave
WorkingDirectory=/var/lib/pizzawave
ExecStart=/opt/pizzawave/pizzad/pizzad --config /etc/pizzawave/pizzad.json
Restart=on-failure
RestartSec=5
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
Environment=HOME=/var/lib/pizzawave
Environment=XDG_CONFIG_HOME=/var/lib/pizzawave/appdata
Environment=XDG_DATA_HOME=/var/lib/pizzawave/appdata

[Install]
WantedBy=multi-user.target
EOF

cat > "$PKG_ROOT/etc/pizzawave/pizzad.json" <<'JSON'
{
  "server": { "httpBind": "0.0.0.0", "httpPort": 8080 },
  "branding": { "stackName": "PizzaWave" },
  "auth": {
    "mode": "none",
    "readRequiresAuth": false,
    "writeRequiresAuth": false,
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
  },
  "locations": {
    "monitoredAreas": [
      {
        "areaId": "hamilton-county-tn",
        "areaLabel": "Hamilton County, TN",
        "systemShortName": "whiteoak-hamilton",
        "aliases": [ "whiteoak-hamilton", "hamilton" ]
      },
      {
        "areaId": "bradley-county-tn",
        "areaLabel": "Bradley County, TN",
        "systemShortName": "bradley",
        "aliases": [ "bradley" ]
      },
      {
        "areaId": "cleveland-tn",
        "areaLabel": "Cleveland, TN",
        "systemShortName": "cleveland",
        "aliases": [ "cleveland" ]
      }
    ]
  },
  "sftpImport": { "enabled": false },
  "setup": {
    "completed": false,
    "wizardVersion": 1,
    "currentStep": "stack"
  }
}
JSON

cat > "$PKG_ROOT/DEBIAN/control" <<EOF
Package: pizzawave
Version: $VERSION
Section: utils
Priority: optional
Architecture: $DEB_ARCH
Maintainer: PizzaWave
Depends: adduser, ca-certificates, python3, systemd
Description: PizzaWave Engine
 Persistent trunk-recorder ingest, transcription, alerting, dashboard API, and web UI.
EOF

cat > "$PKG_ROOT/DEBIAN/conffiles" <<'EOF'
/etc/pizzawave/pizzad.json
EOF

cat > "$PKG_ROOT/DEBIAN/postinst" <<EOF
#!/usr/bin/env bash
set -e

SERVICE_USER="pizzawave"
CONFIG_DIR="/etc/pizzawave"
DATA_DIR="/var/lib/pizzawave"

if ! id "\$SERVICE_USER" >/dev/null 2>&1; then
  adduser --system --group --home "\$DATA_DIR" "\$SERVICE_USER"
fi

mkdir -p "\$CONFIG_DIR" "\$DATA_DIR/audio" "\$DATA_DIR/import-cache" "\$DATA_DIR/appdata"

if [[ ! -f "\$CONFIG_DIR/pizzad.token" ]]; then
  umask 077
  python3 - <<'PY' > "\$CONFIG_DIR/pizzad.token"
import secrets
print(secrets.token_hex(32))
PY
fi

chown -R "\$SERVICE_USER:\$SERVICE_USER" "\$DATA_DIR"
chown -R root:"\$SERVICE_USER" "\$CONFIG_DIR"
chmod 0750 "\$CONFIG_DIR"
chmod 0660 "\$CONFIG_DIR/pizzad.json" || true
chmod 0640 "\$CONFIG_DIR/pizzad.token" || true

if getent group systemd-journal >/dev/null 2>&1; then
  usermod -aG systemd-journal "\$SERVICE_USER" || true
fi
if getent group trunk-recorder >/dev/null 2>&1; then
  usermod -aG trunk-recorder "\$SERVICE_USER" || true
fi

if getent group trunk-recorder >/dev/null 2>&1 && [[ -d /etc/trunk-recorder ]]; then
  chown root:trunk-recorder /etc/trunk-recorder || true
  chmod 0750 /etc/trunk-recorder || true
  for tr_file in /etc/trunk-recorder/config.json /etc/trunk-recorder/talkgroups.csv; do
    if [[ -f "\$tr_file" ]]; then
      chown root:trunk-recorder "\$tr_file" || true
      chmod 0640 "\$tr_file" || true
    fi
  done
fi

cat > /etc/sudoers.d/pizzawave-setup <<'SUDOERS'
pizzawave ALL=(root) NOPASSWD: /usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh *
pizzawave ALL=(root) NOPASSWD: /usr/lib/pizzawave/scripts/tr_tune.sh *
pizzawave ALL=(root) NOPASSWD: /bin/systemctl restart pizzad.service, /usr/bin/systemctl restart pizzad.service
SUDOERS
chmod 0440 /etc/sudoers.d/pizzawave-setup

if command -v systemctl >/dev/null 2>&1; then
  systemctl daemon-reload || true
  systemctl enable pizzad.service || true
  systemctl restart --no-block pizzad.service || true
fi

cat <<'MSG'
PizzaWave Engine installed.
Web UI: http://<tr-server-ip>:8080
Config: /etc/pizzawave/pizzad.json
Token: /etc/pizzawave/pizzad.token
Open the web UI to complete first-run setup. Existing trunk-recorder installs
are detected and configured from the setup wizard; package install does not
modify or restart trunk-recorder.
MSG
EOF

cat > "$PKG_ROOT/DEBIAN/prerm" <<'EOF'
#!/usr/bin/env bash
set -e

if command -v systemctl >/dev/null 2>&1; then
  systemctl stop pizzad.service || true
  systemctl disable pizzad.service || true
fi
EOF

cat > "$PKG_ROOT/DEBIAN/postrm" <<'EOF'
#!/usr/bin/env bash
set -e

if command -v systemctl >/dev/null 2>&1; then
  systemctl daemon-reload || true
fi

if [[ "${1:-}" == "purge" ]]; then
  rm -rf /etc/pizzawave /var/lib/pizzawave
fi
EOF

chmod 0755 "$PKG_ROOT/DEBIAN/postinst" "$PKG_ROOT/DEBIAN/prerm" "$PKG_ROOT/DEBIAN/postrm"
find "$PKG_ROOT/opt/pizzawave/pizzad" -type f -name pizzad -exec chmod 0755 {} +

dpkg-deb --build --root-owner-group "$PKG_ROOT" "$OUTPUT_DIR/$PKG_NAME"

echo "$OUTPUT_DIR/$PKG_NAME"
