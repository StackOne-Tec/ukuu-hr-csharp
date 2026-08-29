import fs from 'node:fs';
import path from 'node:path';

const outDir = path.resolve('demo-video/frames');
fs.mkdirSync(outDir, { recursive: true });

const W = 1920;
const H = 1080;
const FPS = 30;
const DURATION = 60;
const totalFrames = FPS * DURATION;

const clamp = (value, min = 0, max = 1) => Math.min(max, Math.max(min, value));
const mix = (a, b, t) => a + (b - a) * t;
const ease = (t) => {
  t = clamp(t);
  return t < 0.5 ? 4 * t * t * t : 1 - ((-2 * t + 2) ** 3) / 2;
};
const inOut = (frame, start, end) => ease((frame - start) / (end - start));
const fade = (frame, start, end) => clamp((frame - start) / (end - start));
const fmt = (value) => Number(value).toFixed(2);
const opacity = (value) => `opacity="${fmt(value)}"`;
const tr = (x = 0, y = 0, scale = 1, rotate = 0) => `transform="translate(${fmt(x)} ${fmt(y)}) rotate(${fmt(rotate)}) scale(${fmt(scale)})"`;

const font = `font-family="Arial, Helvetica, sans-serif"`;
const mono = `font-family="Courier New, monospace"`;
const xml = (value) => String(value)
  .replaceAll('&', '&amp;')
  .replaceAll('<', '&lt;')
  .replaceAll('>', '&gt;');

function text(x, y, value, size, options = {}) {
  const weight = options.weight ?? 500;
  const fill = options.fill ?? '#F9F7FF';
  const anchor = options.anchor ?? 'start';
  const letter = options.letter ?? 0;
  const extra = options.extra ?? '';
  // Quick Look ignores text-anchor for SVG thumbnails. Pre-position centered labels
  // so the rendered master matches the intended design on the export path.
  const positionedX = anchor === 'middle' ? x - String(value).length * size * .285 : x;
  return `<text x="${positionedX}" y="${y}" ${font} font-size="${size}" font-weight="${weight}" fill="${fill}" text-anchor="start" letter-spacing="${letter}" ${extra}>${xml(value)}</text>`;
}

function monoText(x, y, value, size, options = {}) {
  const fill = options.fill ?? '#A59BB8';
  const anchor = options.anchor ?? 'start';
  const positionedX = anchor === 'middle' ? x - String(value).length * size * .3 : x;
  return `<text x="${positionedX}" y="${y}" ${mono} font-size="${size}" font-weight="${options.weight ?? 500}" fill="${fill}" text-anchor="start" ${options.extra ?? ''}>${xml(value)}</text>`;
}

function rounded(x, y, width, height, radius, fill, stroke = 'none', extra = '') {
  return `<rect x="${x}" y="${y}" width="${width}" height="${height}" rx="${radius}" fill="${fill}" stroke="${stroke}" ${extra}/>`;
}

function line(x1, y1, x2, y2, stroke, width = 1, extra = '') {
  return `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${stroke}" stroke-width="${width}" ${extra}/>`;
}

function circle(cx, cy, r, fill, extra = '') {
  return `<circle cx="${cx}" cy="${cy}" r="${r}" fill="${fill}" ${extra}/>`;
}

function logo(x, y, scale = 1, light = true) {
  const c = light ? '#FAF8FF' : '#25163F';
  const a = '#E2AB3A';
  return `<g ${tr(x, y, scale)}>
    <path d="M0 6 C0 2 3 0 7 0 H17 V32 C17 45 26 51 37 51 C49 51 57 44 57 32 V0 H75 V33 C75 55 60 68 37 68 C14 68 0 55 0 33 Z" fill="${c}"/>
    <circle cx="70" cy="6" r="6" fill="${a}"/>
    ${text(92, 51, 'UKUU', 44, { weight: 800, fill: c, letter: 4 })}
  </g>`;
}

function defs() {
  return `<defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#10091F"/><stop offset="0.55" stop-color="#24133F"/><stop offset="1" stop-color="#0B1126"/></linearGradient>
    <linearGradient id="gold" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#F4C85C"/><stop offset="1" stop-color="#C78019"/></linearGradient>
    <linearGradient id="violet" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#8C65D6"/><stop offset="1" stop-color="#4B2D81"/></linearGradient>
    <linearGradient id="mint" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#63E4BB"/><stop offset="1" stop-color="#14A37F"/></linearGradient>
    <linearGradient id="softPanel" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#FFFFFF"/><stop offset="1" stop-color="#F3F0F8"/></linearGradient>
    <radialGradient id="orb"><stop stop-color="#9B68FF" stop-opacity=".48"/><stop offset=".55" stop-color="#673CA9" stop-opacity=".18"/><stop offset="1" stop-color="#3B1F64" stop-opacity="0"/></radialGradient>
    <radialGradient id="goldOrb"><stop stop-color="#F2C84F" stop-opacity=".34"/><stop offset="1" stop-color="#D59B22" stop-opacity="0"/></radialGradient>
    <filter id="blur"><feGaussianBlur stdDeviation="38"/></filter>
    <filter id="blurSmall"><feGaussianBlur stdDeviation="13"/></filter>
    <filter id="shadow" x="-40%" y="-40%" width="180%" height="180%"><feDropShadow dx="0" dy="28" stdDeviation="25" flood-color="#080412" flood-opacity=".34"/></filter>
    <filter id="softShadow" x="-40%" y="-40%" width="180%" height="180%"><feDropShadow dx="0" dy="12" stdDeviation="13" flood-color="#110B20" flood-opacity=".20"/></filter>
    <pattern id="grid" width="80" height="80" patternUnits="userSpaceOnUse"><path d="M 80 0 L 0 0 0 80" fill="none" stroke="#E9E4F3" stroke-opacity=".055"/></pattern>
    <clipPath id="panelClip"><rect x="0" y="0" width="1440" height="760" rx="28"/></clipPath>
  </defs>`;
}

function background(frame, light = false) {
  const drift = Math.sin(frame / 120) * 45;
  if (light) {
    return `<rect width="${W}" height="${H}" fill="#F4F1F8"/>
      <circle cx="1550" cy="140" r="560" fill="#E2D6FF" opacity=".76" filter="url(#blur)"/>
      <circle cx="260" cy="900" r="470" fill="#FFE5A1" opacity=".58" filter="url(#blur)"/>
      <rect width="${W}" height="${H}" fill="url(#grid)"/>`;
  }
  return `<rect width="${W}" height="${H}" fill="url(#bg)"/>
    <circle cx="${720 + drift}" cy="310" r="540" fill="url(#orb)" filter="url(#blur)"/>
    <circle cx="${1600 - drift}" cy="880" r="470" fill="url(#goldOrb)" filter="url(#blur)"/>
    <rect width="${W}" height="${H}" fill="url(#grid)"/>`;
}

function statusDot(x, y, color, label, pct = 1) {
  return `<g ${opacity(pct)}>${circle(x, y - 4, 5, color)}${text(x + 14, y, label, 15, { fill: '#6B6378', weight: 700 })}</g>`;
}

function desktopShell(x, y, scale = 1, state = 0, frame = 0) {
  const card = (cx, cy, label, value, accent, icon) => {
    return `<g ${tr(cx, cy)}>
      ${rounded(0, 0, 220, 130, 18, '#FFFFFF', '#E8E2EF', 'filter="url(#softShadow)"')}
      ${rounded(18, 18, 38, 38, 12, `${accent}20`)}
      ${text(37, 44, icon, 17, { fill: accent, anchor: 'middle', weight: 800 })}
      ${text(18, 82, value, 30, { fill: '#25163F', weight: 800 })}
      ${text(18, 108, label, 13, { fill: '#7E748D', weight: 700 })}
    </g>`;
  };
  const pulse = (Math.sin(frame / 8) + 1) / 2;
  return `<g ${tr(x, y, scale)} filter="url(#shadow)">
    ${rounded(0, 0, 1440, 760, 28, '#F8F7FB', 'rgba(255,255,255,.6)')}
    <g clip-path="url(#panelClip)">
      ${rounded(0, 0, 238, 760, 0, '#201338')}
      ${logo(32, 34, .34)}
      ${text(32, 139, 'WORKSPACE', 10, { fill: '#AFA3C3', weight: 800, letter: 1.8 })}
      ${navItem(34, 180, 'Overview', '⌂', false)}
      ${navItem(34, 228, 'People', '♙', false)}
      ${navItem(34, 276, 'Attendance', '◷', true)}
      ${navItem(34, 324, 'Time cards', '≡', false)}
      ${navItem(34, 372, 'Reports', '▤', false)}
      ${rounded(24, 643, 190, 86, 16, '#2E1C4A')}
      ${circle(49, 671, 10, '#E5B74C')}
      ${text(66, 668, 'Online today', 12, { fill: '#F9F6FF', weight: 700 })}
      ${text(66, 690, 'All systems operational', 10, { fill: '#BFB2D4' })}
      ${text(278, 74, 'Time & Attendance', 27, { fill: '#25163F', weight: 800 })}
      ${text(278, 102, 'Tuesday, 18 June  ·  live operations', 13, { fill: '#82778F' })}
      ${rounded(1166, 45, 220, 42, 15, '#25163F')}
      ${text(1276, 72, 'Clock in / out', 13, { fill: '#FFF', weight: 700, anchor: 'middle' })}
      ${card(278, 140, 'Present', state > 0 ? '132' : '128', '#14A37F', '✓')}
      ${card(514, 140, 'Late arrivals', state > 1 ? '04' : '07', '#DC7D23', '◷')}
      ${card(750, 140, 'On leave', '06', '#7158B8', '◫')}
      ${card(986, 140, 'Total hours', state > 1 ? '936' : '891', '#2563C9', '↗')}
      ${rounded(278, 302, 1108, 382, 20, '#FFFFFF', '#E8E2EF')}
      ${text(306, 341, 'Today’s attendance', 18, { fill: '#25163F', weight: 800 })}
      ${text(306, 365, 'Shift-aware status, updated as the day unfolds', 12, { fill: '#887D95' })}
      ${rounded(1168, 326, 186, 32, 12, '#F4F1F8')}
      ${text(1261, 347, 'All employees  ▾', 11, { fill: '#5D526A', weight: 700, anchor: 'middle' })}
      ${line(306, 390, 1354, 390, '#EDE8F1')}
      ${tableHeader()}
      ${attendanceRow(426, 'AN', 'Amara N.', 'Product', '08:57', '—', 'On time', '#14A37F', state > 0 ? 1 : .82)}
      ${attendanceRow(480, 'TM', 'Thandi M.', 'Finance', '09:04', '—', state > 1 ? 'Reviewed' : '+4 min late', state > 1 ? '#14A37F' : '#DC7D23', 1)}
      ${attendanceRow(534, 'KM', 'Kito M.', 'Operations', '08:51', '—', 'On time', '#14A37F', .97)}
      ${attendanceRow(588, 'RM', 'Ruth M.', 'Customer care', '09:00', '—', 'On time', '#14A37F', .94)}
      ${attendanceRow(642, 'SW', 'Sizwe W.', 'Engineering', '—', '—', 'Remote', '#7158B8', .9)}
      ${state >= 2 ? `<g ${opacity(.85 + pulse * .15)}>${circle(1328, 450, 11 + pulse * 8, '#14A37F', 'fill-opacity=".14"')}${circle(1328, 450, 5, '#14A37F')}</g>` : ''}
    </g>
  </g>`;
}

function navItem(x, y, label, icon, active) {
  return `<g>
    ${active ? rounded(x - 10, y - 23, 196, 38, 11, '#3A2558') : ''}
    ${text(x, y, icon, 16, { fill: active ? '#F0C052' : '#BBAFD0', weight: 700 })}
    ${text(x + 28, y, label, 13, { fill: active ? '#FFFFFF' : '#C6BCD7', weight: active ? 700 : 500 })}
    ${active ? rounded(x + 155, y - 13, 5, 5, 3, '#E6B743') : ''}
  </g>`;
}

function tableHeader() {
  return `<g>
    ${text(306, 416, 'EMPLOYEE', 10, { fill: '#A095AB', weight: 800, letter: 1.2 })}
    ${text(520, 416, 'SHIFT', 10, { fill: '#A095AB', weight: 800, letter: 1.2 })}
    ${text(756, 416, 'CHECK IN', 10, { fill: '#A095AB', weight: 800, letter: 1.2 })}
    ${text(905, 416, 'CHECK OUT', 10, { fill: '#A095AB', weight: 800, letter: 1.2 })}
    ${text(1072, 416, 'STATUS', 10, { fill: '#A095AB', weight: 800, letter: 1.2 })}
  </g>`;
}

function attendanceRow(y, initials, name, role, checkIn, checkOut, status, color, op) {
  const pillW = Math.max(72, status.length * 7.2 + 25);
  return `<g ${opacity(op)}>
    ${line(306, y + 18, 1354, y + 18, '#F0EDF3')}
    ${circle(325, y - 6, 15, color + '22')}
    ${text(325, y - 1, initials, 9, { fill: color, weight: 800, anchor: 'middle' })}
    ${text(350, y - 4, name, 13, { fill: '#352746', weight: 700 })}
    ${text(350, y + 12, role, 10, { fill: '#958AA0' })}
    ${circle(528, y - 4, 4, '#7C5DC5')}
    ${text(540, y - 1, 'Day shift', 11, { fill: '#5E526B', weight: 600 })}
    ${monoText(756, y + 1, checkIn, 12, { fill: checkIn === '—' ? '#B5ADBE' : '#372A47', weight: 700 })}
    ${monoText(905, y + 1, checkOut, 12, { fill: '#B5ADBE', weight: 700 })}
    ${rounded(1070, y - 20, pillW, 27, 14, color + '19')}
    ${text(1070 + pillW / 2, y - 2, status, 10, { fill: color, anchor: 'middle', weight: 800 })}
  </g>`;
}

function phoneClock(x, y, scale, progress, frame) {
  const p = clamp(progress);
  const scan = 310 + p * 190;
  return `<g ${tr(x, y, scale)} filter="url(#shadow)">
    ${rounded(0, 0, 380, 760, 48, '#121025', '#524166', 'stroke-width="3"')}
    ${rounded(13, 13, 354, 734, 37, 'url(#bg)')}
    ${rounded(142, 28, 96, 24, 13, '#080610')}
    ${logo(43, 85, .22)}
    ${text(190, 183, 'Good morning, Amara', 18, { fill: '#F9F7FF', weight: 700, anchor: 'middle' })}
    ${text(190, 211, 'Tuesday · 18 June', 12, { fill: '#B9AFCB', anchor: 'middle' })}
    <g ${opacity(.28 + .32 * Math.sin(frame / 10) ** 2)}>${circle(190, 368, 145 + p * 28, 'none', 'stroke="#E6B743" stroke-width="2"')}</g>
    ${circle(190, 368, 128, '#2A1B48')}
    ${circle(190, 368, 106, 'url(#violet)')}
    ${circle(190, 368, 80, '#3B2461')}
    <path d="M164 377 C158 362 160 330 177 322 C193 314 210 325 208 342 C207 350 201 354 196 355 M176 400 C181 407 200 409 211 396" fill="none" stroke="#F5D16A" stroke-width="6" stroke-linecap="round"/>
    <path d="M172 365 C179 350 193 347 202 356 M173 383 C182 374 196 374 205 382" fill="none" stroke="#F5D16A" stroke-width="4" stroke-linecap="round"/>
    ${rounded(101, scan, 178, 3, 2, '#F3CA55', '', `filter="url(#blurSmall)" ${opacity(p)}`)}
    ${text(190, 537, p > .83 ? 'You’re clocked in' : 'Tap to clock in', 18, { fill: '#FFF', weight: 700, anchor: 'middle' })}
    ${text(190, 565, p > .83 ? '08:57:42 · on time' : 'Verified in seconds', 12, { fill: p > .83 ? '#68E4BE' : '#BDB2D1', anchor: 'middle', weight: 600 })}
    ${rounded(47, 620, 286, 58, 17, p > .83 ? '#14A37F' : '#E2B748')}
    ${text(190, 657, p > .83 ? 'CLOCKED IN  ✓' : 'CLOCK IN', 14, { fill: p > .83 ? '#FFFFFF' : '#25163F', weight: 800, anchor: 'middle', letter: 1.1 })}
  </g>`;
}

function deviceFlow(x, y, scale, t, frame) {
  const pulse = .25 + .30 * ((Math.sin(frame / 8) + 1) / 2);
  const dotX = 190 + 600 * t;
  return `<g ${tr(x, y, scale)}>
    ${rounded(0, 50, 185, 235, 25, '#28203B', '#5A4970', 'stroke-width="2"')}
    ${rounded(20, 72, 145, 118, 12, '#101020')}
    ${text(92, 120, '08:57', 31, { fill: '#E6B743', weight: 700, anchor: 'middle' })}
    ${text(92, 148, 'Verified entry', 11, { fill: '#D6CEE4', anchor: 'middle' })}
    ${circle(92, 231, 23, '#E3B94E')}
    ${text(92, 236, '✓', 17, { fill: '#25163F', weight: 800, anchor: 'middle' })}
    ${text(92, 332, 'Attendance device', 14, { fill: '#EAE5F3', weight: 700, anchor: 'middle' })}
    ${text(92, 354, 'Hikvision / CSV / API', 11, { fill: '#AFA5BC', anchor: 'middle' })}
    <path d="M215 168 C360 168 380 168 500 168 S620 168 785 168" fill="none" stroke="#6E5C8F" stroke-width="2" stroke-dasharray="7 9"/>
    ${circle(dotX, 168, 10 + pulse * 6, '#F0C054', `${opacity(.2 + pulse)}`)}${circle(dotX, 168, 5, '#FFF0AE')}
    ${rounded(790, 0, 390, 350, 27, '#FAF8FC', '#E8E2F0', 'filter="url(#softShadow)"')}
    ${text(825, 53, 'UKUU Sync', 19, { fill: '#26183D', weight: 800 })}
    ${rounded(1053, 31, 91, 26, 13, '#E4F7F0')}
    ${text(1098, 49, 'LIVE', 10, { fill: '#14A37F', weight: 800, anchor: 'middle', letter: 1.3 })}
    ${rounded(824, 84, 322, 74, 14, '#F4F0F7')}
    ${text(844, 113, 'New event received', 13, { fill: '#645976', weight: 700 })}
    ${monoText(844, 139, 'UKU-042  ·  08:57:42', 12, { fill: '#2A203A', weight: 700 })}
    ${circle(1117, 121, 14, '#DDF5EC')}${text(1117, 126, '✓', 12, { fill: '#159A75', weight: 800, anchor: 'middle' })}
    ${text(825, 204, 'ShiftEngine resolves status', 13, { fill: '#3B2D4D', weight: 700 })}
    ${line(825, 230, 1145, 230, '#E9E4EF')}
    ${statusDot(840, 270, '#14A37F', 'Shift matched')}
    ${statusDot(840, 304, '#14A37F', 'On time')}
    ${statusDot(1005, 270, '#7658B8', 'Audit ready')}
    ${statusDot(1005, 304, '#2580CB', 'Synced')}
  </g>`;
}

function metricsScene(x, y, scale, t, frame) {
  const n = Math.floor(mix(98, 132, t));
  const h = Math.floor(mix(684, 936, t));
  const bar = (bx, value, color, label, index) => {
    const height = value * 1.8;
    const grow = clamp((t - index * .06) / .45);
    return `<g>${rounded(bx, 470 - height * grow, 45, height * grow, 13, color)}${text(bx + 22, 500, label, 11, { fill: '#887D95', anchor: 'middle', weight: 700 })}</g>`;
  };
  const p = .5 + .5 * Math.sin(frame / 13);
  return `<g ${tr(x, y, scale)} filter="url(#shadow)">
    ${rounded(0, 0, 1260, 700, 30, '#FBFAFD', '#ECE7F1')}
    ${text(52, 66, 'Attendance, in real time.', 29, { fill: '#25163F', weight: 800 })}
    ${text(52, 97, 'One view for the people, patterns and exceptions that matter.', 14, { fill: '#857A93' })}
    ${rounded(972, 39, 228, 42, 14, '#F3EFF8')}${text(1086, 66, '18 Jun 2025  ·  Today', 13, { fill: '#554968', weight: 700, anchor: 'middle' })}
    ${metricCard(52, 146, `${n}`, 'present now', '#14A37F', t)}
    ${metricCard(331, 146, `${h}h`, 'total hours', '#2563C9', t)}
    ${metricCard(610, 146, t > .68 ? '98%' : '95%', 'on-time rate', '#7358B7', t)}
    ${metricCard(889, 146, t > .58 ? '4' : '7', 'needs review', '#D97922', t)}
    ${rounded(52, 346, 725, 298, 20, '#F6F3F9')}
    ${text(80, 385, 'Check-ins by hour', 16, { fill: '#342646', weight: 800 })}
    ${text(80, 409, 'Live volume across every connected source', 11, { fill: '#8D8299' })}
    ${line(82, 524, 745, 524, '#E1DBE9')}
    ${bar(120, 42, '#BAA8DE', '07', 0)}${bar(190, 72, '#9D83D3', '08', 1)}${bar(260, 105, '#7A5BC0', '09', 2)}${bar(330, 84, '#8B6DCC', '10', 3)}${bar(400, 54, '#B2A0D9', '11', 4)}${bar(470, 42, '#C3B6E0', '12', 5)}${bar(540, 65, '#A38BD4', '13', 6)}${bar(610, 71, '#8566C3', '14', 7)}
    ${rounded(807, 346, 393, 298, 20, '#261A3D')}
    ${text(838, 388, 'Precision, without friction.', 17, { fill: '#FFF', weight: 800 })}
    ${text(838, 416, 'A complete timeline for every workday.', 12, { fill: '#C9BDD8' })}
    ${line(850, 475, 1140, 475, '#6A577E', 2)}
    ${circle(872, 475, 9 + p * 2, '#E5B94D')}${circle(993, 475, 9, '#61D9B4')}${circle(1120, 475, 9, '#7861B6')}
    ${text(872, 513, '08:57', 12, { fill: '#F5D26B', anchor: 'middle', weight: 800 })}${text(993, 513, '09:04', 12, { fill: '#75E5C0', anchor: 'middle', weight: 800 })}${text(1120, 513, '09:12', 12, { fill: '#BBA8F1', anchor: 'middle', weight: 800 })}
    ${text(872, 540, 'On time', 10, { fill: '#CFC4DB', anchor: 'middle' })}${text(993, 540, 'Late flag', 10, { fill: '#CFC4DB', anchor: 'middle' })}${text(1120, 540, 'Resolved', 10, { fill: '#CFC4DB', anchor: 'middle' })}
    ${rounded(838, 572, 329, 37, 12, '#3A2858')}${text(1002, 596, 'AUTOMATICALLY AUDITED', 10, { fill: '#F2CB5F', anchor: 'middle', weight: 800, letter: 1.3 })}
  </g>`;
}

function metricCard(x, y, value, label, color, t) {
  return `<g><rect x="${x}" y="${y}" width="250" height="146" rx="19" fill="#FFF" stroke="#EAE4F1"/>
    ${circle(x + 36, y + 40, 17, color + '20')}${circle(x + 36, y + 40, 7, color)}
    ${text(x + 26, y + 100, value, 34, { fill: '#271A3E', weight: 800 })}
    ${text(x + 26, y + 123, label, 12, { fill: '#887D95', weight: 700 })}
    ${rounded(x + 187, y + 92, 36, 18, 9, color + '14')}${text(x + 205, y + 105, t > .5 ? '↑' : '…', 11, { fill: color, anchor: 'middle', weight: 800 })}
  </g>`;
}

function auditScene(x, y, scale, progress) {
  const p = clamp(progress);
  return `<g ${tr(x, y, scale)} filter="url(#shadow)">
    ${rounded(0, 0, 1180, 660, 30, '#F9F8FB', '#E8E2EF')}
    ${text(54, 66, 'Exceptions, made accountable.', 29, { fill: '#25163F', weight: 800 })}
    ${text(54, 97, 'Review the context. Make a correction. Keep the record.', 14, { fill: '#857A93' })}
    ${rounded(52, 145, 1076, 150, 18, '#FFF', '#ECE6F1')}
    ${circle(92, 202, 21, '#E9C8B0')}${text(92, 208, 'TM', 12, { fill: '#914A22', weight: 800, anchor: 'middle' })}
    ${text(126, 194, 'Thandi Mumba', 16, { fill: '#302240', weight: 800 })}
    ${text(126, 218, 'Finance · Day shift 08:00–17:00', 12, { fill: '#8A7E96' })}
    ${rounded(426, 178, 126, 43, 12, '#FFF2E8')}${text(489, 205, '09:04  +4 min', 13, { fill: '#C36620', weight: 800, anchor: 'middle' })}
    ${rounded(584, 178, 110, 43, 12, '#F2EFF7')}${text(639, 205, 'Check-in', 13, { fill: '#5E526B', weight: 700, anchor: 'middle' })}
    ${rounded(725, 178, 166, 43, 12, p > .35 ? '#E7F7F0' : '#F2EFF7')}${text(808, 205, p > .35 ? '✓ Approved' : 'Review needed', 13, { fill: p > .35 ? '#148A6B' : '#5E526B', weight: 800, anchor: 'middle' })}
    ${rounded(958, 174, 134, 50, 14, p > .5 ? '#25163F' : '#E8E2EF')}${text(1025, 205, p > .5 ? 'Saved  ✓' : 'Correct', 13, { fill: p > .5 ? '#FFF' : '#4F435C', weight: 800, anchor: 'middle' })}
    ${rounded(52, 332, 510, 270, 20, '#FFF', '#ECE6F1')}
    ${text(80, 373, 'Shift policy', 15, { fill: '#342646', weight: 800 })}
    ${text(80, 399, 'Day shift · 08:00–17:00', 13, { fill: '#63576F', weight: 700 })}
    ${line(80, 432, 530, 432, '#EDE8F1')}
    ${statusDot(90, 473, '#14A37F', '5-minute grace period')}
    ${statusDot(90, 511, '#14A37F', 'Verified clock source')}
    ${statusDot(90, 549, '#7358B7', 'Reason attached')}
    ${rounded(605, 332, 523, 270, 20, '#261A3D')}
    ${text(634, 374, 'Audit trail', 15, { fill: '#FFF', weight: 800 })}
    ${text(634, 402, p > .55 ? '09:08  ·  Correction saved by M. Banda' : '09:04  ·  Attendance event received', 12, { fill: '#D5CADF', weight: 600 })}
    ${line(649, 433, 649, 540, '#6B567E', 2)}
    ${circle(649, 432, 6, '#E5B94D')}${circle(649, 486, 6, '#60D7B2')}${circle(649, 540, 6, '#AB92E0')}
    ${text(671, 437, 'Device timestamp preserved', 12, { fill: '#E7E0EE', weight: 700 })}
    ${text(671, 491, p > .35 ? 'Context reviewed' : 'Awaiting review', 12, { fill: '#E7E0EE', weight: 700 })}
    ${text(671, 545, p > .55 ? 'Change is fully auditable' : 'No detail is lost', 12, { fill: '#E7E0EE', weight: 700 })}
  </g>`;
}

function scene(frame) {
  const seconds = frame / FPS;
  const f = frame;
  if (seconds < 7) {
    const p = inOut(f, 8, 130);
    const p2 = inOut(f, 70, 190);
    const p3 = inOut(f, 128, 250);
    return `${background(f)}
      <circle cx="1000" cy="545" r="430" fill="url(#orb)" filter="url(#blur)"/>
      <g ${tr(734, 324, 1 + .04 * p)} ${opacity(p)}>${logo(0, 0, .78)}</g>
      <g ${opacity(p2)}>${text(960, 585, 'Every minute.', 76, { fill: '#FFF', weight: 800, anchor: 'middle' })}</g>
      <g ${opacity(p3)}>${text(960, 665, 'Accounted for.', 76, { fill: '#F0C257', weight: 800, anchor: 'middle' })}</g>
      <g ${opacity(inOut(f, 180, 260))}>${text(960, 748, 'Attendance intelligence for the way your people work.', 18, { fill: '#CEC3DB', anchor: 'middle', weight: 600 })}</g>
      <g ${opacity(inOut(f, 260, 330))}>${text(960, 965, 'UKUU  /  TIME & ATTENDANCE', 11, { fill: '#B9ABC7', anchor: 'middle', weight: 800, letter: 3 })}</g>`;
  }
  if (seconds < 15) {
    const local = f - 210;
    const p = inOut(local, 0, 95);
    const p2 = inOut(local, 72, 180);
    const scan = inOut(local, 165, 300);
    return `${background(f)}
      <g ${tr(166, 250 - 15 * p)} ${opacity(p)}>
        ${text(0, 0, 'The day starts', 64, { fill: '#FFF', weight: 800 })}
        ${text(0, 75, 'with a moment of trust.', 64, { fill: '#F0C257', weight: 800 })}
        ${text(0, 148, 'A simple clock-in. A reliable record.', 18, { fill: '#D3C8DE', weight: 600 })}
        ${rounded(0, 208, 235, 44, 16, '#FFFFFF16', '#FFFFFF22')}${circle(28, 230, 6, '#65E0BC')}${text(45, 235, 'SECURE · INSTANT · HUMAN', 11, { fill: '#F5F1FB', weight: 800, letter: 1.1 })}
      </g>
      <g ${tr(1215, 135 + 12 * (1 - p2), .82)} ${opacity(p2)}>${phoneClock(0, 0, 1, scan, f)}</g>
      <g ${opacity(inOut(local, 200, 300))}>${circle(1540, 783, 5, '#E6B743')}${text(1560, 789, 'Verified in real time', 13, { fill: '#D8CEDF', weight: 700 })}</g>`;
  }
  if (seconds < 24) {
    const local = f - 450;
    const p = inOut(local, 0, 100);
    const p2 = inOut(local, 80, 195);
    return `${background(f, true)}
      <g ${tr(120, 162)} ${opacity(p)}>
        ${text(0, 0, 'One event.', 54, { fill: '#271A3E', weight: 800 })}
        ${text(0, 65, 'A clear picture.', 54, { fill: '#7658B8', weight: 800 })}
        ${text(0, 126, 'UKUU brings every attendance signal into context.', 17, { fill: '#6E627B', weight: 600 })}
      </g>
      <g ${tr(110, 335, .78)} ${opacity(p2)}>${deviceFlow(0, 0, 1, clamp((local - 150) / 115), f)}</g>
      <g ${opacity(inOut(local, 175, 260))}>
        ${rounded(1270, 816, 430, 74, 18, '#25163F')}
        ${circle(1310, 852, 14, '#65E2BA')}${text(1340, 847, 'From device to decision.', 16, { fill: '#FFF', weight: 800 })}${text(1340, 870, 'Accurate by design.', 12, { fill: '#CFC2DC', weight: 600 })}
      </g>`;
  }
  if (seconds < 35) {
    const local = f - 720;
    const p = inOut(local, 0, 105);
    const p2 = inOut(local, 120, 230);
    return `${background(f, true)}
      <g ${tr(188, 150)} ${opacity(p)}>${desktopShell(0, 0, 1.05, local > 135 ? 2 : local > 80 ? 1 : 0, f)}</g>
      <g ${opacity(p2)}>${rounded(1378, 656, 325, 88, 18, '#25163F', 'none', 'filter="url(#shadow)"')}${text(1410, 694, 'LIVE OPERATIONS', 10, { fill: '#F1C85A', weight: 800, letter: 1.4 })}${text(1410, 720, 'Clarity at a glance.', 17, { fill: '#FFF', weight: 800 })}</g>`;
  }
  if (seconds < 46) {
    const local = f - 1050;
    const p = inOut(local, 0, 90);
    const p2 = inOut(local, 105, 220);
    return `${background(f)}
      <g ${tr(180, 140, 1.21)} ${opacity(p)}>${metricsScene(0, 0, 1, clamp((local - 80) / 170), f)}</g>
      <g ${opacity(p2)}>${text(960, 936, 'See the workday as it happens.', 30, { fill: '#FFF', weight: 800, anchor: 'middle' })}${text(960, 974, 'Spot the pattern. Support the people. Move with confidence.', 15, { fill: '#CFC4DA', anchor: 'middle', weight: 600 })}</g>`;
  }
  if (seconds < 54) {
    const local = f - 1380;
    const p = inOut(local, 0, 95);
    const p2 = inOut(local, 125, 220);
    return `${background(f, true)}
      <g ${tr(370, 178, 1.0)} ${opacity(p)}>${auditScene(0, 0, 1, clamp((local - 100) / 115))}</g>
      <g ${opacity(p2)}>${rounded(126, 870, 1668, 66, 21, '#25163F')}${text(960, 911, 'A trustworthy record is more than a timestamp — it is the context around it.', 17, { fill: '#F7F2FF', weight: 700, anchor: 'middle' })}</g>`;
  }
  const local = f - 1620;
  const p = inOut(local, 0, 110);
  const p2 = inOut(local, 90, 195);
  const p3 = inOut(local, 160, 265);
  return `${background(f)}
    <circle cx="960" cy="520" r="500" fill="url(#orb)" filter="url(#blur)"/>
    <g ${tr(730, 160)} ${opacity(p)}>${logo(0, 0, .85)}</g>
    <g ${opacity(p2)}>${text(960, 400, 'Attendance that moves', 64, { fill: '#FFF', weight: 800, anchor: 'middle' })}${text(960, 475, 'your business forward.', 64, { fill: '#F0C257', weight: 800, anchor: 'middle' })}</g>
    <g ${opacity(p3)}>
      ${rounded(700, 555, 520, 62, 20, '#F0C257')}${text(960, 595, 'UKUU  ·  THE WORKDAY, MADE CLEAR', 14, { fill: '#25163F', weight: 800, anchor: 'middle', letter: 1.2 })}
      ${text(960, 685, 'Clock in. Sync. Understand. Act.', 18, { fill: '#D5CADF', weight: 600, anchor: 'middle' })}
      ${text(960, 945, 'ukuuhr.com', 13, { fill: '#BEB2CB', weight: 700, anchor: 'middle', letter: 2.2 })}
    </g>`;
}

for (let frame = 0; frame < totalFrames; frame += 1) {
  const content = scene(frame);
  // Quick Look rasterizes SVG at a 1.5× device scale. Set a 1280×720 intrinsic
  // canvas while retaining the 1920×1080 viewBox so exported 1920px thumbnails
  // faithfully map to our design coordinates.
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="720" viewBox="0 0 ${W} ${H}">${defs()}${content}</svg>`;
  fs.writeFileSync(path.join(outDir, `frame-${String(frame).padStart(5, '0')}.svg`), svg);
}

console.log(`Rendered ${totalFrames} SVG frames to ${outDir}`);
