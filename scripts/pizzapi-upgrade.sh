#!/bin/bash
# pizzapi-upgrade.sh - Upgrade pizzapi UI on Raspberry Pi
#
# Usage:
#   curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/pizzapi-upgrade.sh | sudo bash
#   # Or specify a version:
#   curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/pizzapi-upgrade.sh | sudo -s bash - v1.0.7

set -e

VERSION="${1:-latest}"
DOWNLOAD_DIR="/tmp"
REQUIRED_DOTNET_MAJOR="9"
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    SUDO="sudo"
fi

install_dotnet_runtime() {
    if command -v dotnet >/dev/null 2>&1 && dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App ${REQUIRED_DOTNET_MAJOR}\."; then
        echo ".NET ${REQUIRED_DOTNET_MAJOR} runtime is already installed."
        return
    fi

    echo "Installing .NET ${REQUIRED_DOTNET_MAJOR} runtime for framework-dependent package..."
    $SUDO apt-get update
    $SUDO apt-get install -y ca-certificates wget gpg

    . /etc/os-release
    case "${ID:-}" in
        debian|raspbian)
            MS_REPO_OS="debian"
            MS_REPO_VERSION="${VERSION_ID:-12}"
            ;;
        ubuntu)
            MS_REPO_OS="ubuntu"
            MS_REPO_VERSION="${VERSION_ID:-24.04}"
            ;;
        *)
            echo "Unsupported OS for automatic .NET install: ${PRETTY_NAME:-unknown}"
            echo "Install dotnet-runtime-${REQUIRED_DOTNET_MAJOR}.0 manually, then rerun this script."
            exit 1
            ;;
    esac

    MS_REPO_DEB="packages-microsoft-prod.deb"
    wget -q "https://packages.microsoft.com/config/${MS_REPO_OS}/${MS_REPO_VERSION}/${MS_REPO_DEB}" -O "${DOWNLOAD_DIR}/${MS_REPO_DEB}"
    $SUDO dpkg -i "${DOWNLOAD_DIR}/${MS_REPO_DEB}"
    rm -f "${DOWNLOAD_DIR}/${MS_REPO_DEB}"

    $SUDO apt-get update
    $SUDO apt-get install -y "dotnet-runtime-${REQUIRED_DOTNET_MAJOR}.0"
}

echo "=========================================="
echo "  PizzaPi UI Upgrade"
echo "=========================================="

# Get latest version if not specified
if [ "$VERSION" = "latest" ]; then
    VERSION=$(curl -s https://api.github.com/repos/lilhoser/pizzawave/releases/latest | grep '"tag_name"' | cut -d'"' -f4)
fi

DEB_FILE="pizzapi_${VERSION#v}_arm64.deb"
DOWNLOAD_URL="https://github.com/lilhoser/pizzawave/releases/download/${VERSION}/${DEB_FILE}"

echo "Version: $VERSION"
echo "Downloading ${DEB_FILE}..."
cd $DOWNLOAD_DIR
wget -q --show-progress "$DOWNLOAD_URL" -O "$DEB_FILE"

if ! dpkg-deb -c "$DEB_FILE" | grep -q '/opt/pizzapi/libcoreclr\.so$'; then
    install_dotnet_runtime
else
    echo "Package is self-contained; no system .NET runtime install needed."
fi

echo ""
echo "Installing..."
$SUDO dpkg -i "$DEB_FILE" || $SUDO apt-get install -f -y

# Setup autostart
AUTOSTART_DIR="$HOME/.config/autostart"
mkdir -p "$AUTOSTART_DIR"

cat > "$AUTOSTART_DIR/pizzapi.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=PizzaPi
Exec=/opt/pizzapi/pizzapi
Path=/opt/pizzapi
Icon=/opt/pizzapi/images/logo.png
Terminal=false
X-GNOME-Autostart-enabled=true
NoDisplay=false
StartupNotify=false
StartupWMClass=pizzapi
EOF

echo "PizzaPi autostart file created at $AUTOSTART_DIR/pizzapi.desktop"

echo ""
echo "=========================================="
echo "  Upgrade Complete!"
echo "=========================================="
echo "Run it from the desktop or terminal:"
echo ""
echo "  /opt/pizzapi/pizzapi"
echo ""
echo "Config: /etc/pizzapi/appsettings.json"
echo ""
