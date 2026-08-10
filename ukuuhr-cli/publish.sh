#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# ukuuhr npm Publish Script
# ═══════════════════════════════════════════════════════════════════
#
# This script publishes the ukuuhr package to the npm registry.
#
# Prerequisites:
#   1. You must have an npm account. If you don't have one:
#      → Visit https://www.npmjs.com/signup
#      → Create account with: username=chungu424, email=chungu424@gmail.com
#   2. You must be logged in: npm login
#
# Usage:
#   chmod +x publish.sh
#   ./publish.sh          # Normal publish
#   ./publish.sh --dry    # Dry run (preview only)
#   ./publish.sh --check  # Check if logged in and package is ready
# ═══════════════════════════════════════════════════════════════════

set -e
cd "$(dirname "$0")"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo ""
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo -e "${BLUE}  ukuuhr — npm Publish Script${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo ""

# Step 1: Check npm login
echo -e "${YELLOW}[1/4] Checking npm authentication...${NC}"
if npm whoami 2>/dev/null; then
    echo -e "${GREEN}✓ Logged in to npm${NC}"
else
    echo -e "${RED}✗ Not logged in to npm${NC}"
    echo ""
    echo -e "${YELLOW}You need an npm account first:${NC}"
    echo "  1. Visit https://www.npmjs.com/signup"
    echo "     Username: chungu424"
    echo "     Email:    chungu424@gmail.com"
    echo "  2. Then run: npm login"
    echo ""
    echo "After logging in, re-run this script."
    exit 1
fi
echo ""

# Step 2: Run tests
echo -e "${YELLOW}[2/4] Running tests...${NC}"
npm test
echo -e "${GREEN}✓ All tests passed${NC}"
echo ""

# Step 3: Check package contents
echo -e "${YELLOW}[3/4] Package contents (dry-run):${NC}"
npm pack --dry-run 2>&1 | tail -20
echo ""

# Step 4: Publish
if [ "$1" = "--dry" ]; then
    echo -e "${YELLOW}[4/4] Dry run — skipping actual publish${NC}"
    echo ""
    echo -e "${BLUE}To publish for real, run: ./publish.sh${NC}"
elif [ "$1" = "--check" ]; then
    echo -e "${YELLOW}[4/4] Check only — skipping publish${NC}"
else
    echo -e "${YELLOW}[4/4] Publishing to npm...${NC}"
    npm publish --access public 2>&1
    echo ""
    echo -e "${GREEN}═══════════════════════════════════════════════════════${NC}"
    echo -e "${GREEN}  ✓ Published successfully!${NC}"
    echo -e "${GREEN}═══════════════════════════════════════════════════════${NC}"
    echo ""
    echo "  Install:  npm install -g ukuuhr"
    echo "  Use:      ukuuhr connect"
    echo "  View:     https://www.npmjs.com/package/ukuuhr"
fi
