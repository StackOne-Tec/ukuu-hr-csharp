/**
 * ANSI color constants for terminal output
 */

const RESET  = '\x1b[0m';
const BOLD   = '\x1b[1m';
const DIM    = '\x1b[2m';
const RED    = '\x1b[31m';
const GREEN  = '\x1b[32m';
const YELLOW = '\x1b[33m';
const BLUE   = '\x1b[34m';
const MAGENTA= '\x1b[35m';
const CYAN   = '\x1b[36m';
const WHITE  = '\x1b[37m';

const BG_RED    = '\x1b[41m';
const BG_GREEN  = '\x1b[42m';
const BG_YELLOW = '\x1b[43m';

module.exports = {
  RESET, BOLD, DIM, RED, GREEN, YELLOW, BLUE, MAGENTA, CYAN, WHITE,
  BG_RED, BG_GREEN, BG_YELLOW
};
