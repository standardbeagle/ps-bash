#!/usr/bin/env bash
set -euo pipefail

# Install ps-bash from GitHub releases.
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/standardbeagle/ps-bash/main/install.sh | bash

VERSION="${VERSION:-latest}"
INSTALL_DIR="${INSTALL_DIR:-$HOME/.local/bin}"
NO_ADD_TO_PATH="${NO_ADD_TO_PATH:-}"

# Detect OS and architecture
OS=""
ARCH=""
case "$(uname -s)" in
    Linux*)     OS=linux ;;
    Darwin*)    OS=osx ;;
    CYGWIN*|MINGW*|MSYS*) OS=win ;;
    *)          echo "Unsupported OS: $(uname -s)"; exit 1 ;;
esac

case "$(uname -m)" in
    x86_64)  ARCH=x64 ;;
    arm64|aarch64) ARCH=arm64 ;;
    *)       echo "Unsupported architecture: $(uname -m)"; exit 1 ;;
esac

RID="${OS}-${ARCH}"
EXT=""
[ "$OS" = "win" ] && EXT=".exe"

BINARY_NAME="ps-bash${EXT}"
TARGET_PATH="${INSTALL_DIR}/${BINARY_NAME}"

# Resolve version
if [ "$VERSION" = "latest" ]; then
    VERSION=$(curl -fsSL "https://api.github.com/repos/standardbeagle/ps-bash/releases/latest" | grep '"tag_name":' | sed -E 's/.*"tag_name": "([^"]+)".*/\1/')
fi

# Strip leading 'v' if present just in case, but GitHub releases use it
if [ "${VERSION#v}" = "$VERSION" ]; then
    VERSION="v${VERSION}"
fi

echo "Installing ps-bash ${VERSION} for ${RID}..."

ASSET_NAME="ps-bash-${VERSION}-${RID}.zip"
DOWNLOAD_URL="https://github.com/standardbeagle/ps-bash/releases/download/${VERSION}/${ASSET_NAME}"
TEMP_DIR=$(mktemp -d)
trap 'rm -rf "$TEMP_DIR"' EXIT

curl -fsSL "$DOWNLOAD_URL" -o "${TEMP_DIR}/${ASSET_NAME}"

mkdir -p "$INSTALL_DIR"
unzip -o "${TEMP_DIR}/${ASSET_NAME}" -d "$TEMP_DIR"
cp "${TEMP_DIR}/${BINARY_NAME}" "$TARGET_PATH"
chmod +x "$TARGET_PATH"

echo "Installed to: $TARGET_PATH"

# Add to PATH
if [ -z "$NO_ADD_TO_PATH" ]; then
    if ! echo "$PATH" | tr ':' '\n' | grep -qx "$INSTALL_DIR"; then
        SHELL_NAME=$(basename "${SHELL:-bash}")
        case "$SHELL_NAME" in
            zsh)
                PROFILE="$HOME/.zshrc"
                ;;
            bash)
                PROFILE="$HOME/.bashrc"
                ;;
            fish)
                PROFILE="$HOME/.config/fish/config.fish"
                mkdir -p "$(dirname "$PROFILE")"
                ;;
            *)
                PROFILE="$HOME/.profile"
                ;;
        esac

        if [ -f "$PROFILE" ]; then
            if ! grep -q "$INSTALL_DIR" "$PROFILE"; then
                echo "" >> "$PROFILE"
                echo "# ps-bash" >> "$PROFILE"
                echo "export PATH=\"$INSTALL_DIR:\$PATH\"" >> "$PROFILE"
                echo "Added $INSTALL_DIR to PATH in $PROFILE"
            fi
        else
            echo "export PATH=\"$INSTALL_DIR:\$PATH\"" > "$PROFILE"
            echo "Created $PROFILE and added $INSTALL_DIR to PATH"
        fi
    else
        echo "$INSTALL_DIR is already in your PATH."
    fi
fi

# Verify
if command -v "$TARGET_PATH" >/dev/null 2>&1; then
    INSTALLED_VERSION=$("$TARGET_PATH" --version 2>/dev/null || true)
    echo "Installed version: $INSTALLED_VERSION"
else
    echo "Installation succeeded but '$TARGET_PATH' is not on your PATH yet."
    echo "Restart your terminal or run: source ~/.bashrc"
fi

echo "Done. Run 'ps-bash --help' to get started."
