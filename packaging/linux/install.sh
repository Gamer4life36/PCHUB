#!/bin/sh
# Adds PCHUB to your app menu (for this user only; no sudo needed).
# Run it from the unpacked PCHUB folder:   ./install.sh
# Remove it again with:                    ./install.sh --remove
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/512x512/apps"
DESKTOP="$APPS/pchub.desktop"

if [ "${1:-}" = "--remove" ]; then
    rm -f "$DESKTOP" "$ICONS/pchub.png"
    echo "PCHUB was removed from the app menu. Delete this folder to remove the program itself."
    exit 0
fi

if [ ! -x "$HERE/PCHUB" ]; then
    chmod +x "$HERE/PCHUB"
fi

mkdir -p "$APPS" "$ICONS"
cp "$HERE/pchub.png" "$ICONS/pchub.png"
cat > "$DESKTOP" <<EOF
[Desktop Entry]
Type=Application
Name=PCHUB
Comment=GPU monitor and launcher for local AI apps
Exec="$HERE/PCHUB" %u
Icon=pchub
Terminal=false
Categories=Utility;System;Monitor;
StartupWMClass=PCHUB
EOF
chmod +x "$DESKTOP"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" >/dev/null 2>&1 || true

echo "PCHUB is in your app menu now."
if ! ldconfig -p 2>/dev/null | grep -q "libwebkit2gtk-4\.[01]"; then
    echo
    echo "To show apps like ComfyUI inside PCHUB, also install WebKitGTK:"
    echo "  Ubuntu, Debian, Mint:  sudo apt install libwebkit2gtk-4.1-0"
    echo "  Fedora:                sudo dnf install webkit2gtk4.1"
    echo "  Arch:                  sudo pacman -S webkit2gtk-4.1"
fi
