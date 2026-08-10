#!/usr/bin/env node

/**
 * Ukuu HR — Post-install setup
 *
 * After `npm install ukuuhr`, this script prints a friendly
 * message guiding the user to connect their Hikvision device.
 */

const C = {
  BOLD: '\x1b[1m', CYAN: '\x1b[36m', GREEN: '\x1b[32m',
  MAGENTA: '\x1b[35m', RESET: '\x1b[0m', WHITE: '\x1b[37m',
};

console.log('');
console.log(`  ${C.MAGENTA}${C.BOLD}╔═══════════════════════════════════════════════╗${C.RESET}`);
console.log(`  ${C.MAGENTA}${C.BOLD}║${C.RESET}     ${C.WHITE}${C.BOLD}UKUU HR — SYNC BRIDGE v2.0${C.RESET}          ${C.MAGENTA}${C.BOLD}║${C.RESET}`);
console.log(`  ${C.MAGENTA}${C.BOLD}╠═══════════════════════════════════════════════╣${C.RESET}`);
console.log(`  ${C.MAGENTA}${C.BOLD}║${C.RESET}  ${C.GREEN}Installed successfully!${C.RESET}                ${C.MAGENTA}${C.BOLD}║${C.RESET}`);
console.log(`  ${C.MAGENTA}${C.BOLD}╚═══════════════════════════════════════════════╝${C.RESET}`);
console.log('');
console.log(`  To connect to your Hikvision device, run:`);
console.log('');
console.log(`    ${C.CYAN}${C.BOLD}npx ukuuhr connect${C.RESET}`);
console.log('');
console.log(`  This will guide you through setting up your device connection.`);
console.log('');
console.log(`  Other commands:`);
console.log(`    ${C.CYAN}npx ukuuhr attendance${C.RESET}    — Pull attendance records`);
console.log(`    ${C.CYAN}npx ukuuhr sync${C.RESET}          — Sync records to cloud`);
console.log(`    ${C.CYAN}npx ukuuhr probe${C.RESET}         — Discover device endpoints`);
console.log(`    ${C.CYAN}npx ukuuhr help${C.RESET}          — Show all commands`);
console.log('');
