import fs from 'node:fs';
import path from 'node:path';

const sampleRate = 44100;
const duration = 60;
const samples = sampleRate * duration;
const left = new Float32Array(samples);
const right = new Float32Array(samples);
const bpm = 104;
const beat = 60 / bpm;

const midi = (n) => 440 * (2 ** ((n - 69) / 12));
const addTone = (start, length, freq, amp, options = {}) => {
  const startIndex = Math.max(0, Math.floor(start * sampleRate));
  const endIndex = Math.min(samples, Math.ceil((start + length) * sampleRate));
  const attack = options.attack ?? .012;
  const release = options.release ?? Math.min(.35, length * .5);
  const pan = options.pan ?? 0;
  const detune = options.detune ?? 0;
  const harmonics = options.harmonics ?? [[1, 1], [2, .16], [3, .05]];
  const vibrato = options.vibrato ?? 0;
  for (let i = startIndex; i < endIndex; i++) {
    const t = (i - startIndex) / sampleRate;
    const life = t / length;
    const env = Math.min(1, t / attack) * Math.min(1, (length - t) / release) * (options.decay ? Math.exp(-t * options.decay) : 1);
    const wobble = vibrato ? Math.sin(Math.PI * 2 * 4.8 * t) * vibrato : 0;
    let value = 0;
    for (const [multiple, level] of harmonics) {
      value += Math.sin(Math.PI * 2 * (freq * (1 + detune + wobble)) * multiple * t) * level;
    }
    value *= amp * env;
    left[i] += value * (1 - pan * .32);
    right[i] += value * (1 + pan * .32);
  }
};

const addNoise = (start, length, amp, pan = 0) => {
  const startIndex = Math.max(0, Math.floor(start * sampleRate));
  const endIndex = Math.min(samples, Math.ceil((start + length) * sampleRate));
  let seed = Math.floor(start * 10000) + 1;
  const rand = () => (seed = (seed * 16807) % 2147483647) / 2147483647 * 2 - 1;
  for (let i = startIndex; i < endIndex; i++) {
    const t = (i - startIndex) / sampleRate;
    const env = Math.min(1, t / .005) * Math.max(0, 1 - t / length) ** 2;
    const value = rand() * amp * env;
    left[i] += value * (1 - pan * .25);
    right[i] += value * (1 + pan * .25);
  }
};

const chords = [
  [50, 57, 62, 66], // Dm9 colour
  [47, 54, 59, 62], // Bm7
  [43, 50, 55, 59], // G6
  [45, 52, 57, 61], // A7
];
const arpeggios = [[62, 66, 69, 74], [59, 62, 66, 71], [55, 59, 62, 67], [57, 61, 64, 69]];

for (let bar = 0; bar < 27; bar++) {
  const start = bar * beat * 4;
  const c = chords[bar % chords.length];
  const arp = arpeggios[bar % arpeggios.length];
  const intensity = bar < 3 ? .42 + bar * .12 : bar > 23 ? .65 - (bar - 23) * .13 : .72;
  c.forEach((note, index) => addTone(start, beat * 3.85, midi(note), .025 * intensity, {
    attack: .7, release: 1.2, pan: (index - 1.5) * .17, harmonics: [[1, 1], [2, .08]], vibrato: .0012,
  }));
  addTone(start, beat * 1.85, midi(c[0] - 12), .074 * intensity, { attack: .025, release: .4, pan: -.04, harmonics: [[1, 1], [.5, .32], [2, .05]] });
  addTone(start + beat * 2, beat * 1.85, midi(c[0] - 12), .068 * intensity, { attack: .025, release: .4, pan: .04, harmonics: [[1, 1], [.5, .28], [2, .05]] });
  for (let step = 0; step < 8; step++) {
    const note = arp[(step + (bar % 2)) % arp.length];
    const t = start + step * beat * .5 + .03;
    addTone(t, beat * .33, midi(note), .032 * intensity, { attack: .006, release: .12, pan: (step % 2 ? .31 : -.25), harmonics: [[1, 1], [2, .22], [3, .05]], decay: 2.2 });
  }
  if (bar >= 3 && bar < 25) {
    for (let step = 0; step < 4; step++) {
      const t = start + step * beat;
      addTone(t, .16, 67, .095 * intensity, { attack: .002, release: .12, harmonics: [[1, 1], [.5, .45], [1.5, .16]], pan: -.02 });
      addNoise(t, .07, .020 * intensity, -.1);
      if (step === 1 || step === 3) addNoise(t, .035, .027 * intensity, .2);
    }
    [1.5, 3.5].forEach((offset) => addNoise(start + offset * beat, .18, .028 * intensity, .35));
  }
}

// Gentle sparkles on major scene changes.
[[6.6, 74], [14.5, 78], [23.4, 81], [34.2, 78], [45.3, 81], [53.3, 86]].forEach(([time, note], i) => {
  addTone(time, 1.8, midi(note), .045, { attack: .006, release: 1.25, pan: i % 2 ? .32 : -.32, harmonics: [[1, 1], [2, .3], [3, .13]], decay: 1.15 });
});

const wav = Buffer.alloc(44 + samples * 4);
wav.write('RIFF', 0);
wav.writeUInt32LE(36 + samples * 4, 4);
wav.write('WAVEfmt ', 8);
wav.writeUInt32LE(16, 16);
wav.writeUInt16LE(1, 20);
wav.writeUInt16LE(2, 22);
wav.writeUInt32LE(sampleRate, 24);
wav.writeUInt32LE(sampleRate * 4, 28);
wav.writeUInt16LE(4, 32);
wav.writeUInt16LE(16, 34);
wav.write('data', 36);
wav.writeUInt32LE(samples * 4, 40);
for (let i = 0; i < samples; i++) {
  const fadeIn = Math.min(1, i / (sampleRate * 1.2));
  const fadeOut = Math.min(1, (samples - i) / (sampleRate * 2.8));
  const l = Math.tanh(left[i] * .88) * fadeIn * fadeOut;
  const r = Math.tanh(right[i] * .88) * fadeIn * fadeOut;
  wav.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(l * 32767))), 44 + i * 4);
  wav.writeInt16LE(Math.max(-32768, Math.min(32767, Math.round(r * 32767))), 46 + i * 4);
}
const output = path.resolve('demo-video/ukuu-attendance-original-score.wav');
fs.writeFileSync(output, wav);
console.log(`Wrote original soundtrack: ${output}`);
