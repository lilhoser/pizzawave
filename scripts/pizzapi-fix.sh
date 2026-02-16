#!/bin/bash
# pizzapi-fix.sh - Quick fix for pizzapi headless mode
# Run this on your Raspberry Pi to upgrade to v1.0.6 with headless mode support

set -e

echo "Upgrading pizzapi to v1.0.6 (with headless mode)..."
echo ""

# Download
wget -q --show-progress https://github.com/lilhoser/pizzawave/releases/download/v1.0.6/pizzapi_1.0.6_arm64.deb -O /tmp/pizzapi_1.0.6_arm64.deb

# Install
sudo dpkg -i /tmp/pizzapi_1.0.6_arm64.deb
sudo apt-get install -f -y

# Restart
sudo systemctl daemon-reload
sudo systemctl restart pizzapi

# Cleanup
rm -f /tmp/pizzapi_1.0.6_arm64.deb

echo ""
echo "Done! Check status: sudo systemctl status pizzapi"
echo "View logs: journalctl -u pizzapi -f"
