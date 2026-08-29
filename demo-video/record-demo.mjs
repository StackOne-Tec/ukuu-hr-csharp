#!/usr/bin/env node
/**
 * record-demo.mjs — Records the UKUU interactive demo as a 1080p 30fps MP4.
 *
 * Usage:   node demo-video/record-demo.mjs
 * Output:  demo-video/UKUU_Demo_Reel.mp4
 */

import { createServer } from 'node:http';
import { readFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { execSync } from 'node:child_process';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, '..');
const HTML_PATH = join(ROOT, 'UkuuHr.Web/wwwroot/ukuu-demo.html');
const FRAMES_DIR = join(__dirname, 'demo-frames');
const OUTPUT = join(__dirname, 'UKUU_Demo_Reel.mp4');

const WIDTH = 1920;
const HEIGHT = 1080;
const FPS = 30;
const DURATION_SECONDS = 62;
const TOTAL_FRAMES = FPS * DURATION_SECONDS;

/* ─── 1. Tiny HTTP server ─── */
function startServer() {
  return new Promise((resolve) => {
    const html = readFileSync(HTML_PATH, 'utf-8');
    const server = createServer((_, res) => {
      res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8' });
      res.end(html);
    });
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      console.log(`[server] http://127.0.0.1:${port}`);
      resolve({ server, port });
    });
  });
}

/* ─── 2. Capture frames with Puppeteer ─── */
async function recordFrames(port) {
  let puppeteer;
  try {
    puppeteer = await import('puppeteer');
  } catch {
    console.log('[install] installing puppeteer...');
    execSync('npm install puppeteer', { cwd: __dirname, stdio: 'inherit' });
    puppeteer = await import('puppeteer');
  }

  const browser = await puppeteer.default.launch({
    headless: 'new',
    args: [
      `--window-size=${WIDTH},${HEIGHT}`,
      '--no-sandbox',
      '--disable-setuid-sandbox',
      '--disable-gpu',
      '--disable-dev-shm-usage',
    ],
  });

  const page = await browser.newPage();
  await page.setViewport({ width: WIDTH, height: HEIGHT, deviceScaleFactor: 1 });

  console.log('[browser] loading demo...');
  await page.goto(`http://127.0.0.1:${port}`, { waitUntil: 'networkidle0', timeout: 30000 });
  await page.evaluate(() => new Promise(r => setTimeout(r, 2000)));

  if (existsSync(FRAMES_DIR)) rmSync(FRAMES_DIR, { recursive: true });
  mkdirSync(FRAMES_DIR, { recursive: true });

  const sceneData = await page.evaluate(() => {
    const scenes = document.querySelectorAll('.scene');
    return Array.from(scenes).map(s => ({
      top: s.offsetTop,
      height: s.offsetHeight,
    }));
  });

  const totalSceneHeight = sceneData.reduce((a, s) => a + s.height, 0);
  console.log(`[record] ${sceneData.length} scenes, ${TOTAL_FRAMES} frames, ${DURATION_SECONDS}s`);

  let frameNum = 0;
  const t0 = Date.now();

  for (let si = 0; si < sceneData.length; si++) {
    const scene = sceneData[si];
    const fraction = scene.height / totalSceneHeight;
    const sceneFrames = Math.max(Math.round(TOTAL_FRAMES * fraction), FPS * 3);

    console.log(`[scene ${si + 1}] ${sceneFrames} frames`);

    for (let f = 0; f < sceneFrames; f++) {
      const progress = f / sceneFrames;
      const scrollY = Math.max(0, scene.top + (scene.height - HEIGHT) * progress);
      await page.evaluate(y => window.scrollTo(0, y), scrollY);
      await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));

      const padded = String(frameNum).padStart(6, '0');
      await page.screenshot({ path: join(FRAMES_DIR, `frame-${padded}.png`), type: 'png' });

      frameNum++;
      if (frameNum % 60 === 0) {
        const elapsed = ((Date.now() - t0) / 1000).toFixed(0);
        const pct = ((frameNum / TOTAL_FRAMES) * 100).toFixed(0);
        console.log(`[record] ${frameNum}/${TOTAL_FRAMES} (${pct}%) — ${elapsed}s`);
      }
    }
  }

  console.log(`[record] ${frameNum} frames captured`);
  await browser.close();
  return frameNum;
}

/* ─── 3. Compile MP4 ─── */
function compileMP4() {
  console.log('[ffmpeg] encoding MP4...');
  const cmd = [
    'ffmpeg', '-y',
    '-framerate', String(FPS),
    '-i', join(FRAMES_DIR, 'frame-%06d.png'),
    '-c:v', 'libx264',
    '-preset', 'slow',
    '-crf', '18',
    '-pix_fmt', 'yuv420p',
    '-movflags', '+faststart',
    OUTPUT,
  ].join(' ');
  execSync(cmd, { stdio: 'inherit', cwd: __dirname });
  console.log(`[ffmpeg] done → ${OUTPUT}`);
}

/* ─── Main ─── */
async function main() {
  console.log('═══════════════════════════════════════════');
  console.log('  UKUU Demo → MP4  (1080p · 30fps)');
  console.log('═══════════════════════════════════════════');

  const { server, port } = await startServer();
  try {
    await recordFrames(port);
    compileMP4();
  } finally {
    server.close();
  }

  console.log('[cleanup] removing frames...');
  rmSync(FRAMES_DIR, { recursive: true });

  console.log('═══════════════════════════════════════════');
  console.log('  ✅ demo-video/UKUU_Demo_Reel.mp4');
  console.log('═══════════════════════════════════════════');
}

main().catch(err => { console.error(err); process.exit(1); });
