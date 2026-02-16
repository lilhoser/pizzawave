#!/bin/bash
# pizzapi-upgrade.sh - Upgrade pizzapi on Raspberry Pi
#
# Usage:
#   curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/pizzapi-upgrade.sh | sudo bash
#   # Or specify a version:
#   curl -sL https://raw.githubusercontent.com/lilhoser/pizzawave/main/scripts/pizzapi-upgrade.sh | sudo -s bash - v1.0.6

set -e

VERSION="${1:-latest}"
DOWNLOAD_DIR="/tmp"

echo "=========================================="
echo "  PizzaPi API Upgrade"
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

echo ""
echo "Installing..."
sudo dpkg -i "$DEB_FILE" || sudo apt-get install -f -y

echo ""
echo "Restarting service..."
sudo systemctl daemon-reload
sudo systemctl restart pizzapi

echo ""
echo "=========================================="
echo "  Upgrade Complete!"
echo "=========================================="
echo ""
sudo systemctl status pizzapi --no-pager -l
echo ""
echo "Logs: journalctl -u pizzapi -f"
echo ""

rm -f "$DEB_FILE"
