#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# UkuuHr Desktop — Cross-Platform Build Script
# ═══════════════════════════════════════════════════════════════════
#
# Builds self-contained single-file executables for:
#   - macOS Apple Silicon (arm64)
#   - macOS Intel (x64)
#   - Windows x64
#   - Linux x64
#
# Usage:
#   ./build-desktop.sh              # Build all targets
#   ./build-desktop.sh mac          # Build macOS arm64 only
#   ./build-desktop.sh mac-intel    # Build macOS x64 only
#   ./build-desktop.sh windows      # Build Windows x64 only
#   ./build-desktop.sh linux        # Build Linux x64 only
#   ./build-desktop.sh --clean      # Clean previous builds
#
# Output:
#   wwwroot/downloads/UkuuHr-macOS-arm64
#   wwwroot/downloads/UkuuHr-macOS-x64
#   wwwroot/downloads/UkuuHr-Windows-x64.exe
#   wwwroot/downloads/UkuuHr-Linux-x64
# ═══════════════════════════════════════════════════════════════════

set -e

# Project paths
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/UkuuHr.Desktop"
WEB_DOWNLOADS="$SCRIPT_DIR/UkuuHr.Web/wwwroot/downloads"
BUILD_DIR="$SCRIPT_DIR/.build/desktop"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
BOLD='\033[1m'
DIM='\033[2m'
NC='\033[0m'

# Ensure dotnet is in PATH
export PATH="$HOME/.dotnet:$PATH"

if ! command -v dotnet &>/dev/null; then
    echo -e "${RED}Error: dotnet SDK not found.${NC}"
    echo "Install .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
fi

echo ""
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo -e "${BLUE}  UkuuHr Desktop — Build Script${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo -e "  .NET SDK: ${BOLD}$(dotnet --version)${NC}"
echo -e "  Project:  ${BOLD}$PROJECT_DIR${NC}"
echo ""

# Clean
if [ "$1" = "--clean" ]; then
    echo -e "${YELLOW}Cleaning previous builds...${NC}"
    rm -rf "$BUILD_DIR"
    dotnet clean "$PROJECT_DIR" -c Release -q 2>/dev/null || true
    echo -e "${GREEN}Clean complete.${NC}"
    exit 0
fi

# Create output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$WEB_DOWNLOADS"
mkdir -p "$WEB_DOWNLOADS/osx-arm64"

# ── Build Function ──────────────────────────────────────────────────
build_target() {
    local RID=$1          # e.g. osx-arm64, win-x64
    local LABEL=$2        # e.g. "macOS Apple Silicon"
    local OUTPUT_NAME=$3  # e.g. UkuuHr-macOS-arm64
    local EXT=$4          # e.g. .exe or empty

    echo ""
    echo -e "${YELLOW}┌─ Building: ${BOLD}$LABEL${NC} (${RID})"
    echo -e "${YELLOW}│${NC}"

    local OUT_DIR="$BUILD_DIR/$RID"
    mkdir -p "$OUT_DIR"

    # dotnet publish
    dotnet publish "$PROJECT_DIR/UkuuHr.Desktop.csproj" \
        -c Release \
        -r "$RID" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -o "$OUT_DIR" \
        2>&1 | while IFS= read -r line; do
            # Only show important lines (errors, warnings)
            if [[ "$line" == *"error"* ]] || [[ "$line" == *"warning"* ]] || [[ "$line" == *"Publishing"* ]] || [[ "$line" == *"UkuuHrSync"* ]]; then
                echo -e "${YELLOW}│${NC} ${DIM}$line${NC}"
            fi
        done

    # Check output
    local BINARY="$OUT_DIR/UkuuHrSync${EXT}"
    if [ -f "$BINARY" ]; then
        local SIZE=$(du -h "$BINARY" | cut -f1)
        local DEST="$WEB_DOWNLOADS/${OUTPUT_NAME}${EXT}"
        cp "$BINARY" "$DEST"

        echo -e "${YELLOW}│${NC}"
        echo -e "${YELLOW}└─${NC} ${GREEN}${BOLD}SUCCESS${NC} — ${OUTPUT_NAME}${EXT} (${SIZE})"
        echo -e "    Output: ${DIM}${DEST}${NC}"

        # For macOS arm64, also copy to osx-arm64 subdirectory
        if [ "$RID" = "osx-arm64" ]; then
            cp "$BINARY" "$WEB_DOWNLOADS/osx-arm64/UkuuHrSync"
            echo -e "    Also:  ${DIM}$WEB_DOWNLOADS/osx-arm64/UkuuHrSync${NC}"
        fi
    else
        echo -e "${YELLOW}│${NC}"
        echo -e "${YELLOW}└─${NC} ${RED}${BOLD}FAILED${NC} — Binary not found at $BINARY"
        return 1
    fi
}

# ── Determine which targets to build ────────────────────────────────
TARGET="${1:-all}"
BUILT=0
FAILED=0

case "$TARGET" in
    mac|macos|arm64)
        build_target "osx-arm64" "macOS Apple Silicon" "UkuuHr-macOS-arm64" "" && ((BUILT++)) || ((FAILED++))
        ;;
    mac-intel|macos-intel|x64-intel)
        build_target "osx-x64" "macOS Intel" "UkuuHr-macOS-x64" "" && ((BUILT++)) || ((FAILED++))
        ;;
    windows|win)
        build_target "win-x64" "Windows x64" "UkuuHr-Windows-x64" ".exe" && ((BUILT++)) || ((FAILED++))
        ;;
    linux)
        build_target "linux-x64" "Linux x64" "UkuuHr-Linux-x64" "" && ((BUILT++)) || ((FAILED++))
        ;;
    all|"")
        # Build all targets
        echo -e "  Building ${BOLD}all targets${NC}..."

        # macOS Apple Silicon
        build_target "osx-arm64" "macOS Apple Silicon" "UkuuHr-macOS-arm64" "" && ((BUILT++)) || ((FAILED++))

        # macOS Intel
        build_target "osx-x64" "macOS Intel" "UkuuHr-macOS-x64" "" && ((BUILT++)) || ((FAILED++))

        # Windows x64
        build_target "win-x64" "Windows x64" "UkuuHr-Windows-x64" ".exe" && ((BUILT++)) || ((FAILED++))

        # Linux x64
        build_target "linux-x64" "Linux x64" "UkuuHr-Linux-x64" "" && ((BUILT++)) || ((FAILED++))
        ;;
    *)
        echo -e "${RED}Unknown target: $TARGET${NC}"
        echo "Usage: $0 [mac|mac-intel|windows|linux|all|--clean]"
        exit 1
        ;;
esac

# ── Summary ─────────────────────────────────────────────────────────
echo ""
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo -e "  ${BOLD}Build Summary${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo ""

# List all output files
for f in "$WEB_DOWNLOADS"/UkuuHr-*; do
    if [ -f "$f" ]; then
        SIZE=$(du -h "$f" | cut -f1)
        NAME=$(basename "$f")
        echo -e "  ${GREEN}✓${NC} ${NAME}  (${SIZE})"
    fi
done

echo ""
if [ "$FAILED" -eq 0 ]; then
    echo -e "  ${GREEN}${BOLD}All builds succeeded!${NC} ($BUILT targets)"
else
    echo -e "  ${RED}${BOLD}$FAILED build(s) failed${NC}, $BUILT succeeded"
fi

echo ""
echo -e "  ${DIM}Binaries are in: $WEB_DOWNLOADS${NC}"
echo -e "  ${DIM}Usage: UkuuHrSync connect  (or UkuuHrSync.exe connect on Windows)${NC}"
echo ""
