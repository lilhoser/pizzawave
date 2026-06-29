#!/usr/bin/env bash
set -euo pipefail

VENV_DIR="${VENV_DIR:-/opt/pizzawave/venv/faster-whisper}"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "setup-faster-whisper.sh must run as root." >&2
  exit 1
fi

echo "Installing Python venv prerequisites..."
if command -v apt-get >/dev/null 2>&1; then
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y python3 python3-venv python3-pip
fi

echo "Creating faster-whisper venv at ${VENV_DIR}..."
mkdir -p "$(dirname "$VENV_DIR")"
python3 -m venv "$VENV_DIR"

echo "Installing pinned faster-whisper runtime..."
"$VENV_DIR/bin/python" -m pip install --upgrade pip
"$VENV_DIR/bin/python" -m pip install --upgrade "faster-whisper==1.2.1"

echo "Validating faster-whisper import..."
"$VENV_DIR/bin/python" - <<'PY'
from faster_whisper import WhisperModel
print("faster-whisper import OK")
PY

chown -R pizzawave:pizzawave "$VENV_DIR" 2>/dev/null || true
echo "faster-whisper support installed."
