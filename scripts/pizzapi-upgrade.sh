#!/bin/bash
# pizzapi-upgrade.sh - Upgrade pizzapi to the latest release
# Run on Raspberry Pi: curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/pizzapi-upgrade.sh | sudo bash

set -e

VERSION="${1:-latest}"
DOWNLOAD_DIR="/tmp"
INSTALL_DIR="/opt/pizzapi"

echo "=========================================="
echo "  PizzaPi API Upgrade Script"
echo "=========================================="

# Get latest version if not specified
if [ "$VERSION" = "latest" ]; then
    VERSION=$(curl -s https://api.github.com/repos/lilhoser/pizzawave/releases/latest | grep '"tag_name"' | cut -d'"' -f4)
    echo "Latest version: $VERSION"
fi

DEB_FILE="pizzapi_${VERSION#v}_arm64.deb"
DOWNLOAD_URL="https://github.com/lilhoser/pizzawave/releases/download/${VERSION}/${DEB_FILE}"

echo ""
echo "Downloading ${DEB_FILE}..."
cd $DOWNLOAD_DIR
wget -q --show-progress "$DOWNLOAD_URL" -O "$DEB_FILE"

echo ""
echo "Installing pizzapi ${VERSION#v}..."
sudo dpkg -i "$DEB_FILE"

echo ""
echo "Fixing dependencies (if needed)..."
sudo apt-get install -f -y

echo ""
echo "Reloading systemd configuration..."
sudo systemctl daemon-reload

echo ""
echo "Restarting pizzapi service..."
sudo systemctl restart pizzapi

echo ""
echo "=========================================="
echo "  Upgrade Complete!"
echo "=========================================="
echo ""
echo "Service status:"
sudo systemctl status pizzapi --no-pager -l
echo ""
echo "View logs: journalctl -u pizzapi -f"
echo "Config:    /etc/pizzapi/appsettings.json"
echo ""

# Cleanup
rm -f "$DOWNLOAD_DIR/$DEB_FILE"
