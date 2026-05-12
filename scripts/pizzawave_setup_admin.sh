#!/usr/bin/env bash
set -euo pipefail

ACTION="${1:-}"
BACKUP_ROOT="${2:-/var/backups/pizzawave}"

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

remove_legacy_pizzapi() {
  echo "Removing legacy pizzapi/tr-health artifacts. trunk-recorder is preserved."
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
  for home in /home/*; do
    [[ -d "$home/.config/autostart" ]] && rm -f "$home/.config/autostart/pizzapi.desktop"
  done
  if command -v dpkg >/dev/null 2>&1 && dpkg -s pizzapi >/dev/null 2>&1; then
    dpkg --purge pizzapi || apt-get remove -y pizzapi || true
  fi
  rm -rf /opt/pizzapi /etc/pizzapi /var/lib/pizzapi
  systemctl daemon-reload
  echo "Legacy pizzapi/tr-health cleanup complete."
}

restart_tr() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  systemctl restart "$unit"
  echo "Restarted $unit."
}

stop_tr() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  systemctl stop "$unit"
  echo "Stopped $unit."
}

start_tr() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  systemctl start "$unit"
  echo "Started $unit."
}

stop_calibration() {
  pkill -TERM -f 'tr_tune.sh' 2>/dev/null || true
  sleep 1
  pkill -KILL -f 'tr_tune.sh' 2>/dev/null || true
  pkill -TERM -f 'trunk-recorder.*--config|trunk-recorder' 2>/dev/null || true
  echo "Requested termination for tr_tune.sh and trunk-recorder calibration processes."
}

patch_callstream() {
  local config_path="${2:-/etc/trunk-recorder/config.json}"
  local host="${3:-127.0.0.1}"
  local port="${4:-9123}"
  local disable_capture_dir="0"
  local restart_unit="${5:-}"
  if [[ "${5:-}" == "1" || "${5:-}" == "0" || "${5:-}" == "true" || "${5:-}" == "false" ]]; then
    disable_capture_dir="${5:-0}"
    restart_unit="${6:-}"
  fi

  python3 - "$config_path" "$host" "$port" "$disable_capture_dir" <<'PY'
import json
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

path = Path(sys.argv[1])
host = sys.argv[2]
port = int(sys.argv[3])
disable_capture_dir = str(sys.argv[4]).lower() in ("1", "true", "yes")
if not path.exists():
    raise SystemExit(f"TR config not found: {path}")

data = json.loads(path.read_text(encoding="utf-8-sig"))
if not isinstance(data, dict):
    raise SystemExit("TR config root must be a JSON object")

backup = path.with_name(path.name + ".bak-" + datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S"))
shutil.copy2(path, backup)

plugins = data.setdefault("plugins", [])
if not isinstance(plugins, list):
    raise SystemExit("TR config plugins field must be an array")

callstream = None
for plugin in plugins:
    if isinstance(plugin, dict) and str(plugin.get("name", "")).lower() == "callstream":
        callstream = plugin
        break
if callstream is None:
    callstream = {}
    plugins.append(callstream)

callstream["name"] = "callstream"
callstream["library"] = callstream.get("library") or "libcallstream.so"
callstream["host"] = host
callstream["port"] = port
if disable_capture_dir:
    data.pop("captureDir", None)

path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
print(backup)
PY

  if [[ -n "$restart_unit" ]]; then
    [[ "$restart_unit" == *.service ]] || restart_unit="${restart_unit}.service"
    systemctl restart "$restart_unit"
    echo "Restarted $restart_unit."
  fi
}

restart_pizzad() {
  systemctl restart pizzad.service
  echo "Restarted pizzad.service."
}

install_sdr_tools() {
  if ! command -v apt-get >/dev/null 2>&1; then
    echo "apt-get not available on this OS."
    return 2
  fi

  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  local gqrx_pkg=""
  for pkg in gqrx-sdr gqrx; do
    if apt-cache show "$pkg" >/dev/null 2>&1; then
      gqrx_pkg="$pkg"
      break
    fi
  done

  if [[ -n "$gqrx_pkg" ]]; then
    echo "Installing SDR tools with GQRX package: $gqrx_pkg"
    apt-get install -y rtl-sdr usbutils "$gqrx_pkg"
  else
    echo "No gqrx/gqrx-sdr apt package found for this OS/architecture. Installing rtl-sdr and usbutils only."
    apt-get install -y rtl-sdr usbutils
    return 3
  fi
}

case "$ACTION" in
  backup-existing-tr)
    backup_existing_tr
    ;;
  remove-legacy-pizzapi)
    remove_legacy_pizzapi
    ;;
  restart-tr)
    restart_tr "$@"
    ;;
  stop-tr)
    stop_tr "$@"
    ;;
  start-tr)
    start_tr "$@"
    ;;
  stop-calibration)
    stop_calibration
    ;;
  patch-callstream)
    patch_callstream "$@"
    ;;
  restart-pizzad)
    restart_pizzad
    ;;
  install-sdr-tools)
    install_sdr_tools
    ;;
  *)
    echo "Usage: $0 {backup-existing-tr|remove-legacy-pizzapi|stop-tr|start-tr|stop-calibration|restart-tr|patch-callstream|restart-pizzad|install-sdr-tools}" >&2
    exit 2
    ;;
esac
