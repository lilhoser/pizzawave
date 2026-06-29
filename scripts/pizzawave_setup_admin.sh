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

remove_legacy_apps() {
  echo "Removing retired PizzaWave app/tr-health artifacts. trunk-recorder is preserved."
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
  echo "Retired app/tr-health cleanup complete."
}

restart_tr() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  rm -f /run/pizzawave/tr-control.json /run/pizzawave/tr-fault.json 2>/dev/null || true
  systemctl reset-failed "$unit" 2>/dev/null || true
  systemctl restart "$unit"
  echo "Restarted $unit."
}

restart_qdrant() {
  systemctl restart qdrant.service
  echo "Restarted qdrant.service."
}

stop_tr() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  systemctl stop "$unit"
  systemctl reset-failed "$unit" 2>/dev/null || true
  rm -f /run/pizzawave/tr-fault.json 2>/dev/null || true
  mkdir -p /run/pizzawave
  python3 - "$unit" /run/pizzawave/tr-control.json <<'PY'
import json
import os
import sys
from datetime import datetime, timezone

unit = sys.argv[1]
dest = sys.argv[2]
payload = {
    "createdAtUtc": datetime.now(timezone.utc).isoformat(),
    "unit": unit,
    "state": "stopped",
    "reason": "operator_stop",
}
tmp = dest + ".tmp"
with open(tmp, "w", encoding="utf-8") as handle:
    json.dump(payload, handle, indent=2)
os.replace(tmp, dest)
PY
  echo "Stopped $unit."
}

install_tr_watchdog() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  local dropin_dir="/etc/systemd/system/${unit}.d"
  mkdir -p "$dropin_dir"
  cat > "$dropin_dir/pizzawave-watchdog.conf" <<EOF
[Unit]
StartLimitIntervalSec=300
StartLimitBurst=6

[Service]
RestartSec=30s
ExecStopPost=+/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh record-tr-fault ${unit}
EOF
  systemctl daemon-reload
  echo "Installed PizzaWave TR watchdog drop-in for $unit."
}

record_tr_fault() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  local dest="/run/pizzawave/tr-fault.json"
  if [[ "${SERVICE_RESULT:-}" == "success" ]]; then
    rm -f "$dest" 2>/dev/null || true
    echo "trunk-recorder stopped cleanly; no fault snapshot recorded."
    return 0
  fi
  mkdir -p "$(dirname "$dest")"
  python3 - "$unit" "$dest" <<'PY'
import json
import os
import subprocess
import sys
from datetime import datetime, timezone

unit = sys.argv[1]
dest = sys.argv[2]

def run(args):
    try:
        return subprocess.run(args, check=False, text=True, capture_output=True, timeout=10).stdout
    except Exception as exc:
        return str(exc)

show = run(["systemctl", "show", unit, "--property=ActiveState,SubState,Result,NRestarts,ExecMainStatus,ExecMainCode,ActiveEnterTimestamp", "--no-page"])
systemd = {}
for line in show.splitlines():
    if "=" in line:
        key, value = line.split("=", 1)
        systemd[key] = value

journal = run(["journalctl", "-u", unit, "-n", "80", "--no-pager", "-o", "short-iso"])
journal_lines = [line for line in journal.splitlines() if line.strip()][-80:]
joined = "\n".join(journal_lines).lower()
signature_patterns = {
    "source_stopped_receiving_samples": "has stopped receiving samples",
    "sdr_open_failed": "failed to open",
    "usb_claim_failed": "usb_claim_interface",
    "airspy_not_found": "airspy_error_not_found",
    "rtlsdr_open_failed": "failed to open rtlsdr",
    "config_parse_failed": "failed parsing config",
    "callstream_error": "callstream",
}
signatures = [name for name, pattern in signature_patterns.items() if pattern in joined]

payload = {
    "createdAtUtc": datetime.now(timezone.utc).isoformat(),
    "unit": unit,
    "serviceResult": os.environ.get("SERVICE_RESULT", ""),
    "exitCode": os.environ.get("EXIT_CODE", ""),
    "exitStatus": os.environ.get("EXIT_STATUS", ""),
    "systemd": systemd,
    "signatures": signatures,
    "journalTail": journal_lines,
}
tmp = dest + ".tmp"
with open(tmp, "w", encoding="utf-8") as handle:
    json.dump(payload, handle, indent=2)
os.replace(tmp, dest)
PY
  echo "Recorded TR fault snapshot at $dest."
}

start_tr() {
  local unit="${2:-trunk-recorder.service}"
  [[ "$unit" == *.service ]] || unit="${unit}.service"
  rm -f /run/pizzawave/tr-control.json /run/pizzawave/tr-fault.json 2>/dev/null || true
  systemctl reset-failed "$unit" 2>/dev/null || true
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

detect_sdrs() {
  bash -lc 'command -v rtl_test >/dev/null 2>&1 && timeout 8 rtl_test -t 2>&1 || true; command -v airspy_info >/dev/null 2>&1 && timeout 8 airspy_info 2>&1 || true'
}

install_tr_file() {
  local src="${2:?source path required}"
  local dest="${3:?destination path required}"
  python3 - "$src" "$dest" <<'PY'
import grp
import json
import os
import pwd
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

src = Path(sys.argv[1])
dest = Path(sys.argv[2])
if not str(dest).startswith("/etc/trunk-recorder/"):
    raise SystemExit(f"Refusing TR destination outside /etc/trunk-recorder: {dest}")
if not src.is_file():
    raise SystemExit(f"TR source does not exist: {src}")

dest.parent.mkdir(parents=True, exist_ok=True)
backup = ""
if dest.exists():
    backup_path = dest.with_name(dest.name + ".bak-" + datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S"))
    shutil.copy2(dest, backup_path)
    backup = str(backup_path)

tmp = dest.with_name(dest.name + f".tmp-{os.getpid()}")
shutil.copy2(src, tmp)
try:
    gid = grp.getgrnam("trunk-recorder").gr_gid
    os.chown(dest.parent, 0, gid)
    os.chmod(dest.parent, 0o750)
    os.chown(tmp, 0, gid)
except KeyError:
    os.chown(tmp, 0, 0)
os.chmod(tmp, 0o640)
os.replace(tmp, dest)

if dest.name == "config.json":
    try:
        config = json.loads(dest.read_text(encoding="utf-8-sig"))
        user = pwd.getpwnam("trunk-recorder")
        group = grp.getgrnam("trunk-recorder")
        temp_dir = Path(str(config.get("tempDir") or "/var/lib/trunk-recorder/tmp"))
        if temp_dir.is_absolute():
            temp_dir.mkdir(parents=True, exist_ok=True)
            os.chown(temp_dir, user.pw_uid, group.gr_gid)
            os.chmod(temp_dir, 0o755)
            for system in config.get("systems", []):
                if not isinstance(system, dict):
                    continue
                short_name = str(system.get("shortName") or "").strip()
                if not short_name or "/" in short_name or short_name in (".", ".."):
                    continue
                site_dir = temp_dir / short_name
                site_dir.mkdir(parents=True, exist_ok=True)
                for root, dirs, files in os.walk(site_dir):
                    os.chown(root, user.pw_uid, group.gr_gid)
                    for name in dirs:
                        os.chown(Path(root) / name, user.pw_uid, group.gr_gid)
                    for name in files:
                        os.chown(Path(root) / name, user.pw_uid, group.gr_gid)
                os.chmod(site_dir, 0o755)
    except Exception as exc:
        print(f"Warning: installed TR config, but could not prepare tempDir ownership: {exc}", file=sys.stderr)
print(backup)
PY
}

restart_pizzad() {
  nohup sh -c 'sleep 1; systemctl restart pizzad.service' >/tmp/pizzawave-restart-pizzad.log 2>&1 &
  echo "Scheduled pizzad.service restart."
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
    apt-get install -y rtl-sdr airspy usbutils "$gqrx_pkg"
  else
    echo "No gqrx/gqrx-sdr apt package found for this OS/architecture. Installing rtl-sdr, Airspy tools, and usbutils only."
    apt-get install -y rtl-sdr airspy usbutils
    return 3
  fi
}

install_diagnostic_tools() {
  if ! command -v apt-get >/dev/null 2>&1; then
    echo "apt-get not available on this OS." >&2
    return 2
  fi

  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  apt-get install -y \
    git cmake build-essential pkg-config doxygen clang-format \
    gnuradio gnuradio-dev gr-osmosdr librtlsdr-dev rtl-sdr \
    libuhd-dev libhackrf-dev liborc-dev libsndfile1-dev libspdlog-dev \
    python3-pybind11 python3-numpy python3-waitress python3-requests \
    gnuplot-x11 usbutils airspy ffmpeg

  install -d -m 0755 /opt/pizzawave/diagnostics
  if [[ ! -d /opt/pizzawave/diagnostics/op25/.git ]]; then
    rm -rf /opt/pizzawave/diagnostics/op25
    git clone --depth 1 https://github.com/boatbod/op25 /opt/pizzawave/diagnostics/op25
  else
    git -C /opt/pizzawave/diagnostics/op25 pull --ff-only
  fi

  cd /opt/pizzawave/diagnostics/op25
  echo "/usr/bin/python3" > op25/gr-op25_repeater/apps/op25_python
  rm -rf build
  mkdir build
  cd build
  cmake ../
  make -j"$(nproc)"
  make install
  ldconfig

  if [[ ! -f /etc/modprobe.d/blacklist-rtl.conf && -f /opt/pizzawave/diagnostics/op25/blacklist-rtl.conf ]]; then
    install -m 0644 /opt/pizzawave/diagnostics/op25/blacklist-rtl.conf /etc/modprobe.d/blacklist-rtl.conf
    echo "Installed RTL DVB driver blacklist. Reboot may be required before SDR tools can claim RTL devices after boot."
  fi

  rm -f /usr/local/bin/rx.py /usr/local/bin/multi_rx.py
  cat >/usr/local/bin/rx.py <<'EOF'
#!/usr/bin/env bash
cd /opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps
export PYTHONPATH="/opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps:/opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps/tdma:${PYTHONPATH:-}"
exec ./rx.py "$@"
EOF
  cat >/usr/local/bin/multi_rx.py <<'EOF'
#!/usr/bin/env bash
cd /opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps
export PYTHONPATH="/opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps:/opt/pizzawave/diagnostics/op25/op25/gr-op25_repeater/apps/tdma:${PYTHONPATH:-}"
exec ./multi_rx.py "$@"
EOF
  chmod 0755 /usr/local/bin/rx.py /usr/local/bin/multi_rx.py
  echo "Installed OP25 diagnostic tools under /opt/pizzawave/diagnostics/op25."
}

install_qdrant() {
  local script="/usr/lib/pizzawave/scripts/setup-qdrant.sh"
  if [[ ! -x "$script" ]]; then
    script="/opt/pizzawave/pizzad/scripts/setup-qdrant.sh"
  fi
  if [[ ! -x "$script" ]]; then
    script="/opt/pizzawave/scripts/setup-qdrant.sh"
  fi
  if [[ ! -x "$script" ]]; then
    echo "setup-qdrant.sh was not found." >&2
    return 2
  fi
  "$script"
}

install_pizzad_config() {
  local candidate="${2:-}"
  local target="${3:-/etc/pizzawave/pizzad.json}"
  if [[ -z "$candidate" ]]; then
    echo "Candidate config path is required." >&2
    return 2
  fi

  local real_candidate real_target backup_dir tmp_target
  real_candidate="$(readlink -f "$candidate")"
  real_target="$(readlink -m "$target")"

  case "$real_candidate" in
    /var/lib/pizzawave/*|/tmp/pizzawave-*) ;;
    *)
      echo "Refusing to install config from untrusted path: $real_candidate" >&2
      return 2
      ;;
  esac

  case "$real_target" in
    /etc/pizzawave/pizzad.json) ;;
    *)
      echo "Refusing to write protected config target: $real_target" >&2
      return 2
      ;;
  esac

  python3 - "$real_candidate" <<'PY'
import json
import sys
from pathlib import Path

path = Path(sys.argv[1])
with path.open("r", encoding="utf-8-sig") as handle:
    data = json.load(handle)
if not isinstance(data, dict):
    raise SystemExit("PizzaWave config root must be a JSON object")
PY

  backup_dir="/var/backups/pizzawave/config-$(date -u +%Y%m%d-%H%M%S)"
  mkdir -p "$backup_dir"
  backup_path "$real_target" "$backup_dir"
  install -d -m 0750 -o root -g pizzawave "$(dirname "$real_target")"
  tmp_target="$(dirname "$real_target")/.pizzad.json.tmp.$$"
  install -m 0660 -o root -g pizzawave "$real_candidate" "$tmp_target"
  mv "$tmp_target" "$real_target"
  echo "Installed $real_target with backup at $backup_dir."
}

apply_staged_restore() {
  local plan="${2:-}"
  if [[ -z "$plan" ]]; then
    echo "Restore plan path is required." >&2
    return 2
  fi

  local real_plan
  real_plan="$(readlink -f "$plan")"
  case "$real_plan" in
    /var/lib/pizzawave/appdata/restore-staging/*/restore-plan.json|/tmp/pizzawave-*/restore-plan.json) ;;
    *)
      echo "Refusing to apply restore plan from untrusted path: $real_plan" >&2
      return 2
      ;;
  esac

  python3 - "$real_plan" <<'PY'
import hashlib
import json
import os
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

plan_path = Path(sys.argv[1]).resolve()
with plan_path.open("r", encoding="utf-8-sig") as handle:
    plan = json.load(handle)
entries = plan.get("entries") or []
if not isinstance(entries, list) or not entries:
    raise SystemExit("Restore plan has no entries.")

allowed_targets = (
    "/etc/pizzawave/",
    "/etc/trunk-recorder/",
    "/var/lib/pizzawave/",
)
allowed_sources = (
    "/var/lib/pizzawave/appdata/restore-staging/",
    "/tmp/pizzawave-",
)

def real(path):
    return str(Path(path).resolve())

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

backup_root = Path("/var/backups/pizzawave/restore-" + datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S"))
for row in entries:
    source = real(row.get("sourcePath", ""))
    target = os.path.realpath(row.get("targetPath", ""))
    if not source.startswith(allowed_sources):
        raise SystemExit(f"Refusing untrusted restore source: {source}")
    if not target.startswith(allowed_targets):
        raise SystemExit(f"Refusing restore target outside PizzaWave/TR paths: {target}")
    if not Path(source).is_file():
        raise SystemExit(f"Restore source missing: {source}")
    expected = str(row.get("sha256", "")).lower()
    if expected and sha256(source) != expected:
        raise SystemExit(f"Checksum mismatch before restore: {source}")

for row in entries:
    source = Path(real(row.get("sourcePath", "")))
    target = Path(os.path.realpath(row.get("targetPath", "")))
    if target.exists():
        backup = backup_root / target.relative_to("/")
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(target, backup)
    if target.name.endswith(".db"):
        for sidecar_suffix in ("-wal", "-shm"):
            sidecar = Path(str(target) + sidecar_suffix)
            if sidecar.exists():
                backup = backup_root / sidecar.relative_to("/")
                backup.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(sidecar, backup)
                sidecar.unlink()
    target.parent.mkdir(parents=True, exist_ok=True)
    tmp = target.parent / (target.name + f".restore-tmp-{os.getpid()}")
    shutil.copy2(source, tmp)
    os.replace(tmp, target)
    if target.name.endswith(".db"):
        for sidecar_suffix in ("-wal", "-shm"):
            sidecar = Path(str(target) + sidecar_suffix)
            if sidecar.exists():
                sidecar.unlink()

config_path = Path("/etc/pizzawave/pizzad.json")
if config_path.exists():
    with config_path.open("r", encoding="utf-8-sig") as handle:
        config = json.load(handle)
    setup = config.setdefault("setup", {})
    setup["completed"] = False
    setup["completedAtUtc"] = None
    setup["currentStep"] = "tr"
    setup["installMode"] = "reuseExistingTr"
    setup["restoreAppliedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    for key in (
        "trDetected",
        "trConfigured",
        "talkgroupsValidated",
        "callstreamValidated",
        "transcriptionValidated",
        "monitoredAreasValidated",
        "healthValidated",
    ):
        setup[key] = False
    setup["pendingRestorePath"] = ""
    setup["pendingRestoreManifestJson"] = ""
    tmp_config = config_path.with_name(config_path.name + f".restore-setup-tmp-{os.getpid()}")
    tmp_config.write_text(json.dumps(config, indent=2) + "\n", encoding="utf-8")
    os.replace(tmp_config, config_path)

print(f"Restore copied {len(entries)} file(s). Previous files were backed up under {backup_root}.")
PY

  chown -R pizzawave:pizzawave /var/lib/pizzawave 2>/dev/null || true
  chown root:pizzawave /etc/pizzawave/pizzad.json 2>/dev/null || true
  chmod 0660 /etc/pizzawave/pizzad.json 2>/dev/null || true
  chown root:pizzawave /etc/pizzawave/pizzad.token 2>/dev/null || true
  chmod 0640 /etc/pizzawave/pizzad.token 2>/dev/null || true
  chown root:root /etc/trunk-recorder/config.json /etc/trunk-recorder/talkgroups.csv 2>/dev/null || true
  systemctl restart qdrant.service 2>/dev/null || true
  systemctl restart trunk-recorder.service 2>/dev/null || true
  nohup sh -c 'sleep 1; systemctl restart pizzad.service' >/tmp/pizzawave-restore-restart-pizzad.log 2>&1 &
  echo "Restore applied. qdrant/trunk-recorder were restarted when present; pizzad restart scheduled."
}

begin_migration() {
  local config="${2:-/etc/pizzawave/pizzad.json}"
  python3 - "$config" <<'PY'
import grp
import json
import os
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

path = Path(sys.argv[1])
if path.resolve() != Path("/etc/pizzawave/pizzad.json"):
    raise SystemExit(f"Refusing migration config outside /etc/pizzawave/pizzad.json: {path}")
data = json.loads(path.read_text(encoding="utf-8-sig"))
setup = data.setdefault("setup", {})
if not setup.get("migrationMode"):
    setup["migrationPreviousCompleted"] = bool(setup.get("completed"))
    setup["migrationPreviousCurrentStep"] = setup.get("currentStep") or ("complete" if setup.get("completed") else "stack")
setup["completed"] = False
setup["currentStep"] = "migration"
setup["migrationMode"] = True
setup["migrationStartedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
backup_dir = Path("/var/backups/pizzawave/migration-" + datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S"))
backup_dir.mkdir(parents=True, exist_ok=True)
shutil.copy2(path, backup_dir / "pizzad.json")
tmp = path.with_name(path.name + f".migration-tmp-{os.getpid()}")
tmp.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
os.chown(tmp, 0, grp.getgrnam("pizzawave").gr_gid)
os.chmod(tmp, 0o660)
os.replace(tmp, path)
print(f"Migration mode enabled. Previous config copied to {backup_dir}.")
PY
  nohup sh -c 'sleep 1; systemctl restart pizzad.service' >/tmp/pizzawave-migration-restart-pizzad.log 2>&1 &
}

cancel_migration() {
  local config="${2:-/etc/pizzawave/pizzad.json}"
  python3 - "$config" <<'PY'
import grp
import json
import os
import sys
from pathlib import Path

path = Path(sys.argv[1])
if path.resolve() != Path("/etc/pizzawave/pizzad.json"):
    raise SystemExit(f"Refusing migration config outside /etc/pizzawave/pizzad.json: {path}")
data = json.loads(path.read_text(encoding="utf-8-sig"))
setup = data.setdefault("setup", {})
reset_applied = bool(setup.get("migrationResetAtUtc"))
setup["migrationMode"] = False
setup["migrationStartedAtUtc"] = None
setup["migrationResetAtUtc"] = None
if reset_applied:
    setup["completed"] = False
    setup["currentStep"] = "tr"
else:
    setup["completed"] = bool(setup.get("migrationPreviousCompleted"))
    setup["currentStep"] = setup.get("migrationPreviousCurrentStep") or ("complete" if setup.get("completed") else "stack")
setup["migrationPreviousCompleted"] = False
setup["migrationPreviousCurrentStep"] = ""
tmp = path.with_name(path.name + f".migration-cancel-tmp-{os.getpid()}")
tmp.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
os.chown(tmp, 0, grp.getgrnam("pizzawave").gr_gid)
os.chmod(tmp, 0o660)
os.replace(tmp, path)
print("Migration mode canceled.")
PY
  nohup sh -c 'sleep 1; systemctl restart pizzad.service' >/tmp/pizzawave-migration-restart-pizzad.log 2>&1 &
}

reset_migration_site_files() {
  local tr_config="${2:-/etc/trunk-recorder/config.json}"
  local talkgroups="${3:-/etc/trunk-recorder/talkgroups.csv}"
  systemctl stop trunk-recorder.service 2>/dev/null || true
  python3 - "$tr_config" "$talkgroups" <<'PY'
import shutil
import sys
from datetime import datetime, timezone
from pathlib import Path

targets = [Path(sys.argv[1]), Path(sys.argv[2])]
backup_dir = Path("/var/backups/pizzawave/migration-site-" + datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S"))
backup_dir.mkdir(parents=True, exist_ok=True)
for target in targets:
    if not str(target).startswith("/etc/trunk-recorder/"):
        raise SystemExit(f"Refusing to reset site file outside allowed paths: {target}")
    if target.exists():
        shutil.copy2(target, backup_dir / target.name)
        target.unlink()
print(f"Site-specific TR files reset. Previous files were backed up under {backup_dir}.")
PY
}

install_auth_token() {
  local src="${2:?source token path required}"
  local dest="${3:-/etc/pizzawave/pizzad.token}"
  python3 - "$src" "$dest" <<'PY'
import grp
import os
import shutil
import sys
from pathlib import Path

src = Path(sys.argv[1])
dest = Path(sys.argv[2])
if dest.resolve() != Path("/etc/pizzawave/pizzad.token"):
    raise SystemExit(f"Refusing token destination outside /etc/pizzawave/pizzad.token: {dest}")
if not src.exists():
    raise SystemExit(f"Token source does not exist: {src}")
dest.parent.mkdir(parents=True, exist_ok=True)
tmp = dest.with_name(dest.name + f".tmp-{os.getpid()}")
shutil.copy2(src, tmp)
os.chown(tmp, 0, grp.getgrnam("pizzawave").gr_gid)
os.chmod(tmp, 0o640)
os.replace(tmp, dest)
print(f"Installed auth token at {dest}.")
PY
}

case "$ACTION" in
  backup-existing-tr)
    backup_existing_tr
    ;;
  remove-legacy-apps)
    remove_legacy_apps
    ;;
  restart-tr)
    restart_tr "$@"
    ;;
  restart-qdrant)
    restart_qdrant
    ;;
  stop-tr)
    stop_tr "$@"
    ;;
  start-tr)
    start_tr "$@"
    ;;
  install-tr-watchdog)
    install_tr_watchdog "$@"
    ;;
  record-tr-fault)
    record_tr_fault "$@"
    ;;
  stop-calibration)
    stop_calibration
    ;;
  patch-callstream)
    patch_callstream "$@"
    ;;
  detect-sdrs)
    detect_sdrs
    ;;
  install-tr-file)
    install_tr_file "$@"
    ;;
  restart-pizzad)
    restart_pizzad
    ;;
  install-sdr-tools)
    install_sdr_tools
    ;;
  install-diagnostic-tools)
    install_diagnostic_tools
    ;;
  install-qdrant)
    install_qdrant
    ;;
  install-pizzad-config)
    install_pizzad_config "$@"
    ;;
  apply-staged-restore)
    apply_staged_restore "$@"
    ;;
  begin-migration)
    begin_migration "$@"
    ;;
  cancel-migration)
    cancel_migration "$@"
    ;;
  reset-migration-site-files)
    reset_migration_site_files "$@"
    ;;
  install-auth-token)
    install_auth_token "$@"
    ;;
  *)
    echo "Usage: $0 {backup-existing-tr|remove-legacy-apps|stop-tr|start-tr|install-tr-watchdog|record-tr-fault|stop-calibration|restart-tr|restart-qdrant|patch-callstream|detect-sdrs|install-tr-file|restart-pizzad|install-sdr-tools|install-diagnostic-tools|install-qdrant|install-pizzad-config|apply-staged-restore|begin-migration|cancel-migration|reset-migration-site-files|install-auth-token}" >&2
    exit 2
    ;;
esac
