#!/usr/bin/env bash
set -euo pipefail

QDRANT_VERSION="${QDRANT_VERSION:-v1.18.0}"
INSTALL_BIN="${INSTALL_BIN:-/usr/local/bin/qdrant}"
CONFIG_PATH="${CONFIG_PATH:-/etc/pizzawave/qdrant.yaml}"
DATA_ROOT="${DATA_ROOT:-/var/lib/pizzawave/qdrant}"
SERVICE_PATH="${SERVICE_PATH:-/etc/systemd/system/qdrant.service}"
SERVICE_USER="${SERVICE_USER:-pizzawave}"
SERVICE_GROUP="${SERVICE_GROUP:-pizzawave}"
ALLOW_UNSUPPORTED_ARM_PAGE_SIZE="${ALLOW_UNSUPPORTED_ARM_PAGE_SIZE:-0}"

arch="$(uname -m)"
case "$arch" in
  x86_64|amd64) asset="qdrant-x86_64-unknown-linux-gnu.tar.gz" ;;
  aarch64|arm64) asset="qdrant-aarch64-unknown-linux-musl.tar.gz" ;;
  *) echo "Unsupported architecture for Qdrant native install: $arch" >&2; exit 2 ;;
esac

url="https://github.com/qdrant/qdrant/releases/download/${QDRANT_VERSION}/${asset}"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  if command -v adduser >/dev/null 2>&1; then
    adduser --system --group --home /var/lib/pizzawave "$SERVICE_USER"
  else
    useradd --system --home-dir /var/lib/pizzawave --user-group "$SERVICE_USER"
  fi
fi

mkdir -p "$(dirname "$CONFIG_PATH")" "$DATA_ROOT" "$DATA_ROOT/snapshots"
chown -R "$SERVICE_USER:$SERVICE_GROUP" "$DATA_ROOT"

if command -v docker >/dev/null 2>&1; then
  if docker ps -a --format '{{.Names}}' 2>/dev/null | grep -qx 'pizzawave-qdrant'; then
    echo "Removing old Docker Qdrant container pizzawave-qdrant."
    docker rm -f pizzawave-qdrant >/dev/null 2>&1 || true
  fi
fi

need_download=1
if [[ -x "$INSTALL_BIN" ]]; then
  if "$INSTALL_BIN" --version 2>/dev/null | grep -q "${QDRANT_VERSION#v}"; then
    need_download=0
  fi
fi

page_size="$(getconf PAGESIZE 2>/dev/null || echo 4096)"
if [[ "$need_download" == "1" && "$asset" == qdrant-aarch64-* && "$page_size" != "4096" && "$ALLOW_UNSUPPORTED_ARM_PAGE_SIZE" != "1" ]]; then
  cat >&2 <<EOF
Official Qdrant ARM64 binaries use jemalloc and are not compatible with this
kernel page size (${page_size} bytes). Install a Qdrant ${QDRANT_VERSION}
binary built with JEMALLOC_SYS_WITH_LG_PAGE=14 at ${INSTALL_BIN}, or boot a
4 KB page-size kernel, then rerun this installer.
EOF
  exit 3
fi

if [[ "$need_download" == "1" ]]; then
  echo "Downloading Qdrant ${QDRANT_VERSION} for ${arch}."
  if command -v curl >/dev/null 2>&1; then
    curl -fL "$url" -o "$tmp/qdrant.tar.gz"
  else
    wget -O "$tmp/qdrant.tar.gz" "$url"
  fi
  tar -xzf "$tmp/qdrant.tar.gz" -C "$tmp"
  install -m 0755 "$tmp/qdrant" "$INSTALL_BIN"
fi

cat > "$CONFIG_PATH" <<EOF
storage:
  storage_path: ${DATA_ROOT}
  snapshots_path: ${DATA_ROOT}/snapshots

service:
  host: 127.0.0.1
  http_port: 6333
  grpc_port: 6334

telemetry_disabled: true
EOF

chmod 0644 "$CONFIG_PATH"

cat > "$SERVICE_PATH" <<EOF
[Unit]
Description=Qdrant Vector Database
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_GROUP}
WorkingDirectory=${DATA_ROOT}
ExecStart=${INSTALL_BIN} --config-path ${CONFIG_PATH}
Restart=on-failure
RestartSec=5
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable qdrant.service
systemctl restart qdrant.service

for _ in $(seq 1 30); do
  if curl -fsS http://127.0.0.1:6333/ >/dev/null 2>&1; then
    echo "Qdrant is running on 127.0.0.1:6333."
    exit 0
  fi
  sleep 1
done

systemctl status qdrant.service --no-pager || true
exit 1
