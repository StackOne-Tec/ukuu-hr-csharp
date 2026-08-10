/**
 * Settings management for Ukuu HR CLI
 *
 * Loads from ~/.ukuuhr/settings.json or creates interactively on first run.
 */

const fs = require('fs');
const path = require('path');
const readline = require('readline');
const { BOLD, CYAN, GREEN, DIM, RESET } = require('./colors');

const DEFAULT_SETTINGS = {
  deviceIp: '192.168.1.137',
  devicePort: 80,
  useHttps: false,
  deviceUsername: 'admin',
  devicePassword: '',
  cloudUrl: 'https://ukuuhr.com',
  apiKey: null,
  syncIntervalMinutes: 5,
};

function getSettingsDir() {
  const home = process.env.HOME || process.env.USERPROFILE || process.env.HOMEPATH || '~';
  return path.join(home, '.ukuuhr');
}

function getSettingsPath(configPath) {
  if (configPath) return configPath;
  return path.join(getSettingsDir(), 'settings.json');
}

function isValid(settings) {
  return settings.deviceIp && settings.deviceUsername && settings.cloudUrl;
}

/**
 * Load settings from file, or create interactively
 */
function loadOrCreateSettings(configPath, headless = false) {
  const filePath = getSettingsPath(configPath);

  // Try to load existing
  if (fs.existsSync(filePath)) {
    try {
      const json = fs.readFileSync(filePath, 'utf-8');
      const loaded = JSON.parse(json);
      if (loaded && isValid(loaded)) {
        console.log(`${DIM}  Loaded: ${filePath}${RESET}`);
        return { ...DEFAULT_SETTINGS, ...loaded };
      }
    } catch (ex) {
      console.log(`  WARNING: Could not load settings.json: ${ex.message}`);
    }
  }

  // Headless mode: create defaults
  if (headless || !process.stdout.isTTY) {
    const defaults = { ...DEFAULT_SETTINGS };
    try {
      const dir = path.dirname(filePath);
      if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(filePath, JSON.stringify(defaults, null, 2));
      console.log(`  Created default settings at: ${filePath}`);
    } catch {}
    return defaults;
  }

  // Interactive setup
  return null; // Signal that interactive setup is needed
}

/**
 * Interactive first-time setup
 */
async function interactiveSetup(configPath) {
  const filePath = getSettingsPath(configPath);

  console.log('\n  First-time setup — enter your Hikvision device details:\n');

  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
  });

  const question = (prompt, defaultVal) => new Promise((resolve) => {
    rl.question(`  ${prompt} [${defaultVal}]: `, (answer) => {
      resolve(answer.trim() || defaultVal);
    });
  });

  const questionMasked = (prompt) => new Promise((resolve) => {
    rl.question(`  ${prompt}: `, (answer) => {
      resolve(answer);
    });
  });

  const ip = await question('Device IP Address', '192.168.1.137');
  const portStr = await question('Port', '80');
  const port = parseInt(portStr, 10) || 80;

  const httpsStr = await question('Use HTTPS? (y/n)', 'n');
  const useHttps = httpsStr.toLowerCase() === 'y';

  const user = await question('Username', 'admin');
  const pass = await questionMasked('Password');
  const cloudUrl = await question('Cloud URL', 'https://ukuuhr.com');
  const apiKey = await questionMasked('API Key (leave empty if not set)');
  const intervalStr = await question('Sync interval in minutes', '5');
  const interval = parseInt(intervalStr, 10) || 5;

  rl.close();

  const settings = {
    deviceIp: ip,
    devicePort: port,
    useHttps,
    deviceUsername: user,
    devicePassword: pass,
    cloudUrl,
    apiKey: apiKey || null,
    syncIntervalMinutes: interval,
  };

  // Save
  try {
    const dir = path.dirname(filePath);
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(filePath, JSON.stringify(settings, null, 2));
    console.log(`\n  ${GREEN}Settings saved to: ${filePath}${RESET}`);
  } catch (ex) {
    console.log(`\n  WARNING: Could not save settings: ${ex.message}`);
  }

  return settings;
}

/**
 * Save settings to file
 */
function saveSettings(settings, configPath) {
  const filePath = getSettingsPath(configPath);
  try {
    const dir = path.dirname(filePath);
    if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(filePath, JSON.stringify(settings, null, 2));
    return true;
  } catch (ex) {
    console.log(`  WARNING: Could not save settings: ${ex.message}`);
    return false;
  }
}

module.exports = {
  DEFAULT_SETTINGS,
  loadOrCreateSettings,
  interactiveSetup,
  saveSettings,
  getSettingsPath,
  isValid,
};
