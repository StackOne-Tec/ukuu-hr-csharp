#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# npm Quick Setup — Creates account and logs in
# ═══════════════════════════════════════════════════════════════════
#
# Step 1: If you don't have an npm account yet:
#   → Open https://www.npmjs.com/signup in your browser
#   → Fill in: Username, Email, Password
#   → Verify your email
#
# Step 2: Run this script to log in:
#   ./npm-setup.sh
#
# Step 3: Publish:
#   ./publish.sh
# ═══════════════════════════════════════════════════════════════════

set -e

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo ""
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo -e "${BLUE}  ukuuhr — npm Account Setup${NC}"
echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
echo ""

# Check if already logged in
if npm whoami 2>/dev/null; then
    echo -e "${GREEN}✓ Already logged in to npm!${NC}"
    echo ""
    echo "You can now publish:"
    echo "  cd $(dirname "$0")"
    echo "  ./publish.sh"
    exit 0
fi

echo -e "${YELLOW}You need to create an npm account and log in.${NC}"
echo ""
echo "Step 1: Create your npm account"
echo "  → Open this URL in your browser:"
echo "    https://www.npmjs.com/signup"
echo ""
echo "  → Use these details:"
echo "    Username:  chungu424"
echo "    Email:     chungu424@gmail.com"
echo "    Password:  (your choice)"
echo ""
echo "  → Verify your email address"
echo ""
echo "Step 2: Log in via CLI"
echo "  → Run: npm login"
echo "  → Or use web login: npm login --auth-type=web"
echo ""
echo "Step 3: After logging in, run the publish script:"
echo "  → ./publish.sh"
echo ""

# Try to start the web login flow
echo -e "${BLUE}Starting npm web login...${NC}"
echo -e "${YELLOW}(Complete the login in your browser, then come back here)${NC}"
echo ""
npm login --auth-type=web
