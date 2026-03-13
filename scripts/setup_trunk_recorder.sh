#!/usr/bin/env bash
# =============================================================================
# Trunk Recorder + callstream Setup & Audit Script (Ubuntu 24.04)
#
# Always run with sudo for build, service, clean operations.
# --status can be run without sudo for partial results.
#
# Required for --service or full setup: --config <path-to-your-config.json>
# --clean now wipes /etc/trunk-recorder (including config.json)
#
# Usage examples:
# sudo ./setup_trunk_recorder.sh --config ~/myconfig.json
# sudo ./setup_trunk_recorder.sh --clean
# sudo ./setup_trunk_recorder.sh --clean --config ~/fresh.json --build --service
# sudo ./setup_trunk_recorder.sh --status
# =============================================================================
set -euo pipefail

# ────────────────────────────────────────────────────────────────────────────
# CONSTANTS - Defined FIRST to satisfy set -u
# ────────────────────────────────────────────────────────────────────────────
USER_NAME="trunk-recorder"
ETC_TR_DIR="/etc/trunk-recorder"
CONFIG_PATH="${ETC_TR_DIR}/config.json"
TALKGROUPS_PATH="${ETC_TR_DIR}/talkgroups.csv"
SERVICE_PATH="/etc/systemd/system/trunk-recorder.service"
VAR_LIB="/var/lib/trunk-recorder"
VAR_LOG="/var/log/trunk-recorder"
PLUGIN_DIR_NAME="callstream"
PLUGIN_SO="/usr/local/lib/trunk-recorder/lib${PLUGIN_DIR_NAME}.so"
BINARY="/usr/local/bin/trunk-recorder"
TRUNK_REPO="https://github.com/TrunkRecorder/trunk-recorder.git"
PLUGIN_REPO="https://github.com/lilhoser/callstream.git"

# Detect real user's home (works with sudo)
if [ -n "${SUDO_USER:-}" ]; then
    REAL_USER="$SUDO_USER"
    REAL_HOME=$(getent passwd "$SUDO_USER" | cut -d: -f6 || echo "/home/$SUDO_USER")
else
    REAL_USER="$(whoami)"
    REAL_HOME="$HOME"
fi

SOURCE_DIR="${REAL_HOME}/tr5/trunk-recorder"
BUILD_DIR="${REAL_HOME}/tr5/trunk-build"

# TMUX live logs viewer
TMUX_SCRIPT="/usr/local/bin/trunklogs.sh"
TMUX_SESSION_NAME="trunklogs"
PROFILE_FILE="${REAL_HOME}/.profile"

# ────────────────────────────────────────────────────────────────────────────
# Functions
# ────────────────────────────────────────────────────────────────────────────
run_status_audit() {
    echo "┌──────────────────────────────────────────────────────────────┐"
    echo "│ Trunk Recorder Installation Audit                            │"
    echo "└──────────────────────────────────────────────────────────────┘"
    echo "Real user home detected: $REAL_HOME"
    echo
    echo "→ System user:"
    if id "$USER_NAME" &>/dev/null; then
        getent passwd "$USER_NAME" | awk -F: '{printf " OK - Exists: %s (UID %s, Home: %s, Shell: %s)\n", $1, $3, $6, $7}'
    else
        echo " MISSING - User '$USER_NAME' does not exist"
    fi
    echo -e "\n→ Key directories:"
    for d in "$VAR_LIB" "$VAR_LOG" "$VAR_LIB/recordings" "$VAR_LIB/tmp"; do
        if [[ -d "$d" ]]; then
            ls -ld "$d" | awk '{printf " OK - %s (owner: %s, perms: %s)\n", $9, $3, $1}'
        else
            echo " MISSING - $d"
        fi
    done
    echo -e "\n→ /etc/trunk-recorder directory:"
    if [[ -d "$ETC_TR_DIR" ]]; then
        echo " OK - Exists"
        ls -l "$ETC_TR_DIR" 2>/dev/null | head -n 5 || echo " (contents listed above)"
    else
        echo " MISSING - $ETC_TR_DIR"
    fi
    echo -e "\n→ Config file:"
    if [[ -f "$CONFIG_PATH" ]]; then
        ls -l "$CONFIG_PATH" | awk '{printf " OK - Exists (owner: %s, perms: %s, size: %s bytes)\n", $3, $1, $5}'
    else
        echo " MISSING - $CONFIG_PATH"
    fi
    echo -e "\n→ Talkgroups file:"
    if [[ -f "$TALKGROUPS_PATH" ]]; then
        ls -l "$TALKGROUPS_PATH" | awk '{printf " OK - Exists (owner: %s, perms: %s, size: %s bytes)\n", $3, $1, $5}'
    else
        echo " MISSING - $TALKGROUPS_PATH"
    fi
    echo -e "\n→ Binary & plugin:"
    for f in "$BINARY" "$PLUGIN_SO"; do
        if [[ -f "$f" ]]; then
            ls -l "$f" | awk '{printf " OK - %s (size: %s bytes, modified: %s %s)\n", $9, $5, $6, $7}'
        else
            echo " MISSING - $f"
        fi
    done
    echo -e "\n→ Source & build directories:"
    for d in "$SOURCE_DIR" "$BUILD_DIR"; do
        if [[ -d "$d" ]]; then
            echo " OK - $d exists"
            if [[ -d "$SOURCE_DIR/user_plugins/$PLUGIN_DIR_NAME" ]]; then
                echo " → callstream plugin present in user_plugins/"
            fi
        else
            echo " MISSING - $d"
        fi
    done
    echo -e "\n→ Systemd service:"
    if [[ -f "$SERVICE_PATH" ]]; then
        echo " OK - Exists"
        systemctl is-enabled trunk-recorder.service &>/dev/null && echo " Enabled" || echo " NOT enabled"
        systemctl is-active trunk-recorder.service &>/dev/null && echo " ACTIVE" || echo " INACTIVE/FAILED"
    else
        echo " MISSING - $SERVICE_PATH"
    fi
	echo -e "\n→ SDR device access for user:"
	if id "$USER_NAME" &>/dev/null; then
		if groups "$USER_NAME" | grep -q plugdev; then
			echo " OK - $USER_NAME is in plugdev group (required for most USB SDRs)"
		else
			echo " WARNING - $USER_NAME is NOT in plugdev group → USB SDR access may fail (permission denied)"
			echo "   Fix: sudo usermod -aG plugdev $USER_NAME  (then restart service)"
		fi
	else
		echo " MISSING - User '$USER_NAME' does not exist"
	fi
    echo -e "\n→ Recent logs (last 20 lines):"
    journalctl -u trunk-recorder -n 20 --no-pager 2>/dev/null || echo " (no logs or service not present)"
    echo -e "\n→ Live tmux log viewer (trunklogs):"
    if tmux has-session -t "$TMUX_SESSION_NAME" 2>/dev/null; then
        echo " OK - tmux session '$TMUX_SESSION_NAME' is running"
        tmux list-sessions | grep "$TMUX_SESSION_NAME" || true
    else
        echo " MISSING - tmux session '$TMUX_SESSION_NAME' not found"
    fi
    echo "→ Helper script:"
    if [[ -f "$TMUX_SCRIPT" ]]; then
        ls -l "$TMUX_SCRIPT" | awk '{printf " OK - Exists (%s %s)\n", $5, $6}'
    else
        echo " MISSING - $TMUX_SCRIPT"
    fi
    echo "→ Crontab @reboot entry:"
    if crontab -u "$REAL_USER" -l 2>/dev/null | grep -q "trunklogs.sh"; then
        echo " OK - @reboot entry present"
    else
        echo " MISSING - no @reboot entry for trunklogs.sh"
    fi
    echo
    echo "Audit complete."
}

print_usage() {
    cat << EOF
Usage: sudo $0 [OPTIONS] --config <path-to-config.json>
Required when using --service or full setup:
  --config PATH               Path to your config.json
  --talkgroups-file PATH      Path to your talkgroups.csv (or equivalent)
Options:
  --clean                     Wipe service, /var folders, /etc/trunk-recorder (prompts)
  --build                     Clone/update repos + build + install
  --service                   Create user, dirs, copy config, create systemd service
  --dry-run                   Setup but DO NOT start/enable service
  --status                    Audit current installation (read-only)
No flags + --config = safe full setup (--build + --service)
EOF
    exit 1
}

# Parse flags
DO_CLEAN=0
DO_BUILD=0
DO_SERVICE=0
DRY_RUN=0
DO_STATUS=0
CONFIG_SOURCE=""
TALKGROUPS_SOURCE=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --clean) DO_CLEAN=1 ;;
        --build) DO_BUILD=1 ;;
        --service) DO_SERVICE=1 ;;
        --dry-run) DRY_RUN=1 ;;
        --status) DO_STATUS=1 ;;
        --talkgroups-file)
            shift
            TALKGROUPS_SOURCE="$1"
            ;;
        --config)
            shift
            CONFIG_SOURCE="$1"
            ;;
        *) print_usage ;;
    esac
    shift
done

# Default: full safe setup if --config given but no action flags
if [[ $DO_CLEAN -eq 0 && $DO_BUILD -eq 0 && $DO_SERVICE -eq 0 && $DO_STATUS -eq 0 && -n "$CONFIG_SOURCE" ]]; then
    DO_BUILD=1
    DO_SERVICE=1
fi

# Require config & talkgroups for service/build actions
if [[ $DO_SERVICE -eq 1 || ( $DO_BUILD -eq 1 && $DO_SERVICE -eq 0 && $DO_CLEAN -eq 0 && $DO_STATUS -eq 0 ) ]]; then
    if [[ -z "$CONFIG_SOURCE" || ! -f "$CONFIG_SOURCE" ]]; then
        echo "Error: --config <path> is required and must exist."
        print_usage
    fi
    if [[ -z "$TALKGROUPS_SOURCE" || ! -f "$TALKGROUPS_SOURCE" ]]; then
        echo "Error: --talkgroups-file <path> is required and must exist."
        print_usage
    fi
fi

# Root check (allow --status without sudo)
if [[ $EUID -ne 0 && $DO_STATUS -eq 0 ]]; then
    echo "This script must be run with sudo (except --status)."
    exit 1
fi

# ────────────────────────────────────────────────────────────────────────────
# STATUS AUDIT
# ────────────────────────────────────────────────────────────────────────────
if [[ $DO_STATUS -eq 1 ]]; then
    run_status_audit
    exit 0
fi

# ────────────────────────────────────────────────────────────────────────────
# CLEANUP
# ────────────────────────────────────────────────────────────────────────────
if [[ $DO_CLEAN -eq 1 ]]; then
    echo
    echo "!!! WARNING: --clean selected !!!"
    echo "This will DELETE:"
    echo " - systemd service"
    echo " - $VAR_LIB (recordings, tmp)"
    echo " - $VAR_LOG"
    echo " - $ETC_TR_DIR (config + files)"
    echo
    read -p "Type YES (uppercase) to continue: " confirm
    if [[ "$confirm" != "YES" ]]; then
        echo "Aborted."
        exit 0
    fi

    systemctl stop trunk-recorder.service 2>/dev/null || true
    systemctl disable trunk-recorder.service 2>/dev/null || true
    rm -f "$SERVICE_PATH"
    rm -rf "$VAR_LIB" "$VAR_LOG" "$ETC_TR_DIR"

    echo "Cleaning tmux live log viewer..."
    tmux kill-session -t "$TMUX_SESSION_NAME" 2>/dev/null || true
    rm -f "$TMUX_SCRIPT"

    # Remove crontab entry
    (crontab -u "$REAL_USER" -l 2>/dev/null | grep -v "trunklogs.sh" || true) | crontab -u "$REAL_USER" -

    echo "Cleanup complete."
fi

# ────────────────────────────────────────────────────────────────────────────
# BUILD PHASE
# ────────────────────────────────────────────────────────────────────────────
if [[ $DO_BUILD -eq 1 ]]; then
    echo "=== Installing dependencies ==="
    apt update -qq
    apt install -y --no-install-recommends \
        build-essential cmake git pkg-config \
        libboost-all-dev libssl-dev libcurl4-openssl-dev libsndfile1-dev \
        gnuradio gnuradio-dev gr-osmosdr \
        libhackrf-dev librtlsdr-dev libairspy-dev libbladerf-dev libuhd-dev \
        portaudio19-dev tmux

    echo "=== Trunk Recorder ==="
    if [[ ! -d "$SOURCE_DIR" ]]; then
        git clone "$TRUNK_REPO" "$SOURCE_DIR"
    else
        git -C "$SOURCE_DIR" pull --ff-only
    fi

    echo "=== callstream plugin ==="
    USER_PLUGINS_DIR="$SOURCE_DIR/user_plugins"
    PLUGIN_DIR="$USER_PLUGINS_DIR/callstream"
    mkdir -p "$USER_PLUGINS_DIR"
    if [[ ! -d "$PLUGIN_DIR" ]]; then
        git clone "$PLUGIN_REPO" "$PLUGIN_DIR"
    else
        git -C "$PLUGIN_DIR" pull --ff-only
    fi

    # Clean old plugins/ location if exists
    rm -rf "$SOURCE_DIR/plugins/callstream" 2>/dev/null || true

    echo "=== Building ==="
    mkdir -p "$BUILD_DIR"
    cd "$BUILD_DIR"
    cmake "$SOURCE_DIR"
    make -j$(nproc)
    make install
    ldconfig
    echo "Build complete."
fi

# ────────────────────────────────────────────────────────────────────────────
# SERVICE SETUP + TMUX LOG VIEWER
# ────────────────────────────────────────────────────────────────────────────
if [[ $DO_SERVICE -eq 1 ]]; then
    echo "=== Ensuring trunk-recorder user and home ==="
    mkdir -p /var/lib/trunk-recorder/home

    if ! id "$USER_NAME" &>/dev/null; then
        adduser --system --group --home /var/lib/trunk-recorder/home "$USER_NAME"
    else
        usermod -d /var/lib/trunk-recorder/home "$USER_NAME"
    fi

    echo "=== Ensuring trunk-recorder has SDR device access (plugdev group) ==="
    if ! groups "$USER_NAME" | grep -q plugdev; then
        usermod -aG plugdev "$USER_NAME"
        echo " → Added $USER_NAME to plugdev group"
    else
        echo " → $USER_NAME already in plugdev group"
    fi

    chown -R "$USER_NAME:$USER_NAME" /var/lib/trunk-recorder/home
    chmod 700 /var/lib/trunk-recorder/home

    echo "=== Preparing directories ==="
    mkdir -p "$ETC_TR_DIR" "$VAR_LIB"/{recordings,tmp} "$VAR_LOG"
    chown -R "$USER_NAME:$USER_NAME" "$VAR_LIB" "$VAR_LOG" "$ETC_TR_DIR"
    chmod -R 755 "$VAR_LIB" "$VAR_LOG"
    chmod 755 "$VAR_LIB/recordings" "$VAR_LOG"
    chmod -R 644 "$VAR_LIB" "$VAR_LOG"

    echo "=== Copying config ==="
    rm -f "$CONFIG_PATH"
    cp -v --preserve=mode,timestamps "$CONFIG_SOURCE" "$CONFIG_PATH"
    chown "$USER_NAME:$USER_NAME" "$CONFIG_PATH"
    chmod 640 "$CONFIG_PATH"

    echo "=== Copying talkgroups ==="
    rm -f "$TALKGROUPS_PATH"
    cp -v --preserve=mode,timestamps "$TALKGROUPS_SOURCE" "$TALKGROUPS_PATH"
    chown "$USER_NAME:$USER_NAME" "$TALKGROUPS_PATH"
    chmod 640 "$TALKGROUPS_PATH"

    echo "=== Creating systemd service ==="
    cat > "$SERVICE_PATH" << EOF
[Unit]
Description=Trunk Recorder - Trunked Radio Call Recording Daemon
After=network.target network-online.target sound.target

[Service]
Type=simple
User=$USER_NAME
Group=$USER_NAME
ExecStart=$BINARY --config=$CONFIG_PATH
WorkingDirectory=$VAR_LIB
Restart=always
RestartSec=10
UMask=022
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload

    if [[ $DRY_RUN -eq 1 ]]; then
        echo "=== DRY-RUN MODE ==="
        echo "Service file created — not started/enabled."
        echo "Test: sudo -u $USER_NAME $BINARY --config=$CONFIG_PATH"
        echo "Enable: sudo systemctl enable --now trunk-recorder.service"
    else
        systemctl enable trunk-recorder.service
        systemctl start trunk-recorder.service
        echo "Service enabled and started."
        sleep 8
    fi

    # ───────────────────────────────────────────────────────────────
    # LIVE TMUX LOG VIEWER SETUP
    # ───────────────────────────────────────────────────────────────
    echo "=== Setting up live tmux log viewer (trunklogs) ==="

    cat > "$TMUX_SCRIPT" << 'EOF'
#!/usr/bin/env bash
# /usr/local/bin/trunklogs.sh — (re)start trunklogs tmux session

SESSION="trunklogs"

tmux has-session -t "$SESSION" 2>/dev/null && tmux kill-session -t "$SESSION"

tmux new-session -d -s "$SESSION" \
    "journalctl -u trunk-recorder -f --output=short-iso --no-hostname"

tmux set-option -t "$SESSION" history-limit 50000
tmux set-option -t "$SESSION" status on
tmux set-option -t "$SESSION" status-right "#(date +%H:%M) | trunk-recorder logs"

echo "trunklogs session (re)started at $(date)"
EOF

    chmod +x "$TMUX_SCRIPT"

    # Idempotent crontab
    (crontab -u "$REAL_USER" -l 2>/dev/null | grep -v "trunklogs.sh" || true; \
     echo "@reboot   $TMUX_SCRIPT") | crontab -u "$REAL_USER" -

    # ───────────────────────────────────────────────────────────────
    # GNOME Startup Applications entry for persistent graphical tmux logs
    # (opens gnome-terminal with trunklogs on desktop login / reboot)
    # ───────────────────────────────────────────────────────────────
    echo "=== Adding persistent GNOME autostart entry for live logs viewer ==="

    AUTOSTART_DIR="${REAL_HOME}/.config/autostart"
    AUTOSTART_FILE="${AUTOSTART_DIR}/trunklogs-viewer.desktop"

    mkdir -p "$AUTOSTART_DIR"
    chown "$REAL_USER:$REAL_USER" "$AUTOSTART_DIR" 2>/dev/null || true

    cat > "$AUTOSTART_FILE" << EOF
[Desktop Entry]
Type=Application
Name=Trunk Recorder Live Logs
Comment=Auto-open tmux session with trunk-recorder journalctl output
Exec=gnome-terminal --title="Trunk Recorder • Live Logs" --geometry=132x48 -- tmux attach-session -t trunklogs
Icon=utilities-terminal
Terminal=false
Categories=Utility;
StartupNotify=false
X-GNOME-Autostart-enabled=true
EOF

    chown "$REAL_USER:$REAL_USER" "$AUTOSTART_FILE" 2>/dev/null || true
    chmod 644 "$AUTOSTART_FILE" 2>/dev/null || true

    if [[ -f "$AUTOSTART_FILE" ]]; then
        echo " → Created/updated autostart entry: $AUTOSTART_FILE"
        echo "   → Will open graphical terminal on next GNOME login (after reboot or logout/login)"
    else
        echo " → Failed to create autostart .desktop file (check permissions on $REAL_HOME/.config)"
    fi
    
    # Start it now
    "$TMUX_SCRIPT" || echo "Warning: tmux start failed (may need reboot if no session manager)"

    # ───────────────────────────────────────────────────────────────
    # Graphical attach attempt (if desktop session active)
    # ───────────────────────────────────────────────────────────────
    if command -v gnome-terminal >/dev/null 2>&1 && [[ -n "${DISPLAY:-}" ]]; then
        echo "=== Launching gnome-terminal with live logs ==="
        gnome-terminal \
            --title="Trunk Recorder • Live Logs" \
            --geometry=132x48 \
            -- tmux attach -t trunklogs 2>/dev/null || \
        gnome-terminal \
            --title="Trunk Recorder Logs" \
            -- bash -c 'echo "tmux session \"trunklogs\" not found yet."; echo "Should start on reboot."; echo; journalctl -u trunk-recorder -n 15 --no-pager; read -p "Press Enter..."'
    fi

    if [[ $DRY_RUN -ne 1 ]]; then
        echo
        echo "══════════════════════════════════════════════════════════════"
        echo " Post-install status check"
        echo "══════════════════════════════════════════════════════════════"
        run_status_audit
    fi
fi

echo
echo "=== Script finished ==="
if [[ $DRY_RUN -eq 1 ]]; then
    echo "Dry-run complete — service prepared but not active."
else
    echo "Check: sudo systemctl status trunk-recorder"
fi

echo "Run tmux attach -t trunklogs to see trunk-recorder live output"
