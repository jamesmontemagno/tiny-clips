// Synthesizes the original soundtrack for the hype video: 128 BPM, 24 bars = 45.0 s, F minor.
// Everything is generated from oscillators and noise here, so there is nothing to license.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const SR = 44100;
const BPM = 128;
const BEAT = 60 / BPM;
const BAR = BEAT * 4;
const S16 = BEAT / 4;
const BARS = 24;
const N = Math.round(BARS * BAR * SR);

const bus = () => [new Float32Array(N), new Float32Array(N)];
const drums = bus();
const music = bus();
const fx = bus();
const delaySend = bus();
const reverbSend = bus();

let seed = 0x7c11b5;
const rnd = () => {
  seed ^= seed << 13; seed >>>= 0;
  seed ^= seed >>> 17;
  seed ^= seed << 5; seed >>>= 0;
  return seed / 0xffffffff * 2 - 1;
};

const mtof = (m) => 440 * 2 ** ((m - 69) / 12);
const at = (bar, beat = 0) => bar * BAR + beat * BEAT;
const clamp = (x, a, b) => Math.min(b, Math.max(a, x));
const lerp = (a, b, t) => a + (b - a) * clamp(t, 0, 1);

function add(target, i, s, pan = 0) {
  if (i < 0 || i >= N) return;
  target[0][i] += s * Math.min(1, 1 - pan);
  target[1][i] += s * Math.min(1, 1 + pan);
}

// Zero-delay-feedback state variable filter.
function svf() {
  let ic1 = 0, ic2 = 0;
  return (x, cutoff, q, mode) => {
    const g = Math.tan(Math.PI * Math.min(cutoff, SR * 0.45) / SR);
    const k = 1 / q;
    const a1 = 1 / (1 + g * (g + k));
    const a2 = g * a1;
    const a3 = g * a2;
    const v3 = x - ic2;
    const v1 = a1 * ic1 + a2 * v3;
    const v2 = ic2 + a2 * ic1 + a3 * v3;
    ic1 = 2 * v1 - ic1;
    ic2 = 2 * v2 - ic2;
    if (mode === 'lp') return v2;
    if (mode === 'bp') return v1;
    return x - k * v1 - v2;
  };
}

function polyblep(t, dt) {
  if (t < dt) { t /= dt; return t + t - t * t - 1; }
  if (t > 1 - dt) { t = (t - 1) / dt; return t * t + t + t + 1; }
  return 0;
}

function sawOsc(freq, phase = Math.abs(rnd())) {
  const dt = freq / SR;
  return () => {
    const v = 2 * phase - 1 - polyblep(phase, dt);
    phase += dt;
    if (phase >= 1) phase -= 1;
    return v;
  };
}

function kick(t0, gain = 1) {
  const start = Math.round(t0 * SR);
  const len = Math.round(0.42 * SR);
  let phase = 0;
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    const f = 44 + 150 * Math.exp(-t * 34);
    phase += 2 * Math.PI * f / SR;
    const body = Math.sin(phase) * Math.exp(-t * 8.5);
    const click = rnd() * Math.exp(-t * 500) * 0.35;
    add(drums, start + n, Math.tanh((body + click) * 1.8) * 0.9 * gain);
  }
}

function clap(t0, gain = 1) {
  const start = Math.round(t0 * SR);
  const len = Math.round(0.3 * SR);
  const bp = svf();
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    let env = 0;
    for (const off of [0, 0.011, 0.023]) {
      if (t >= off) env += Math.exp(-(t - off) * (off === 0.023 ? 26 : 180));
    }
    const s = bp(rnd(), 1700, 1.4, 'bp') * env * 0.55 * gain;
    add(drums, start + n, s, 0.05);
    add(reverbSend, start + n, s * 0.35);
  }
}

function hat(t0, gain = 1, open = false, pan = 0) {
  const start = Math.round(t0 * SR);
  const decay = open ? 16 : 85;
  const len = Math.round((open ? 0.26 : 0.07) * SR);
  const hp = svf();
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    add(drums, start + n, hp(rnd(), 7800, 0.8, 'hp') * Math.exp(-t * decay) * 0.26 * gain, pan);
  }
}

function snareRoll(t0, t1, gainFrom, gainTo) {
  // Hits accelerate from 8ths to 32nds across the roll.
  let t = t0;
  while (t < t1 - 0.001) {
    const p = (t - t0) / (t1 - t0);
    const start = Math.round(t * SR);
    const len = Math.round(0.09 * SR);
    const bp = svf();
    const g = lerp(gainFrom, gainTo, p);
    let phase = 0;
    for (let n = 0; n < len; n++) {
      const tt = n / SR;
      phase += 2 * Math.PI * 190 / SR;
      const s = (bp(rnd(), 2400, 0.9, 'bp') * 0.8 + Math.sin(phase) * 0.35) * Math.exp(-tt * 55) * g;
      add(drums, start + n, s);
      add(reverbSend, start + n, s * 0.2);
    }
    t += p < 0.5 ? BEAT / 2 : p < 0.8 ? S16 : S16 / 2;
  }
}

function bassNote(t0, midi, dur, gain = 1) {
  const start = Math.round(t0 * SR);
  const len = Math.round((dur + 0.06) * SR);
  const f = mtof(midi);
  const saw = sawOsc(f, 0);
  const lp = svf();
  let phase = 0;
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    phase += 2 * Math.PI * f / SR;
    const env = Math.min(1, t * 300) * (t < dur ? 1 : Math.exp(-(t - dur) * 70));
    const cutoff = 140 + 900 * Math.exp(-t * 14);
    const s = (Math.sin(phase) * 0.75 + lp(saw(), cutoff, 1.6, 'lp') * 0.5) * env * 0.5 * gain;
    add(music, start + n, s);
  }
}

// Detuned saw stack through a lowpass. cutoffFn(t, dur) returns Hz.
function synthNote(target, t0, midi, dur, gain, cutoffFn, opts = {}) {
  const { pan = 0, release = 0.12, attack = 0.004, q = 1.1, voices = 3, detune = 0.11, send = 0, rev = 0 } = opts;
  const start = Math.round(t0 * SR);
  const len = Math.round((dur + release * 4) * SR);
  const oscs = [];
  for (let v = 0; v < voices; v++) {
    const spread = voices === 1 ? 0 : (v / (voices - 1) - 0.5) * 2;
    oscs.push(sawOsc(mtof(midi + spread * detune)));
  }
  const lp = svf();
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    let s = 0;
    for (const o of oscs) s += o();
    s /= voices;
    const env = Math.min(1, t / attack) * (t < dur ? 1 : Math.exp(-(t - dur) / release));
    const out = lp(s, cutoffFn(t, dur), q, 'lp') * env * gain;
    add(target, start + n, out, pan);
    if (send) add(delaySend, start + n, out * send, pan);
    if (rev) add(reverbSend, start + n, out * rev);
  }
}

function riser(t0, t1, gain = 1) {
  const start = Math.round(t0 * SR);
  const len = Math.round((t1 - t0) * SR);
  const bp = svf();
  let phase = 0;
  for (let n = 0; n < len; n++) {
    const p = n / len;
    const cutoff = 280 * (30 ** p);
    const noise = bp(rnd(), cutoff, 2.2, 'bp') * p * p * 0.5;
    phase += 2 * Math.PI * (160 * (5 ** p)) / SR;
    const tone = Math.sin(phase) * p * p * 0.09;
    const s = (noise + tone) * gain;
    add(fx, start + n, s, Math.sin(p * 9) * 0.4);
    add(reverbSend, start + n, s * 0.25);
  }
}

function impact(t0, gain = 1) {
  const start = Math.round(t0 * SR);
  const len = Math.round(2.4 * SR);
  const lp = svf();
  let phase = 0;
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    phase += 2 * Math.PI * (36 + 60 * Math.exp(-t * 7)) / SR;
    const sub = Math.sin(phase) * Math.exp(-t * 2.6) * 0.75;
    const crash = lp(rnd(), 9000 * Math.exp(-t * 1.6) + 600, 0.7, 'lp') * Math.exp(-t * 2.8) * 0.4;
    add(fx, start + n, (sub + crash) * gain, 0);
    add(reverbSend, start + n, crash * 0.6 * gain);
  }
}

function crash(t0, gain = 1) {
  const start = Math.round(t0 * SR);
  const len = Math.round(1.3 * SR);
  const hp = svf();
  for (let n = 0; n < len; n++) {
    const t = n / SR;
    const s = hp(rnd(), 5200, 0.7, 'hp') * Math.exp(-t * 3.6) * 0.2 * gain;
    add(fx, start + n, s, Math.sin(n * 0.0007) * 0.5);
    add(reverbSend, start + n, s * 0.3);
  }
}

// ---- Arrangement -----------------------------------------------------------------------------

const CHORDS = [
  { root: 29, notes: [53, 56, 60, 65] }, // Fm
  { root: 37, notes: [53, 56, 61, 65] }, // Db
  { root: 32, notes: [51, 56, 60, 63] }, // Ab
  { root: 39, notes: [51, 55, 58, 63] }, // Eb
];
const LEAD = [
  [72, 0, 75, 77, 0, 75, 72, 0],
  [73, 0, 77, 80, 0, 77, 73, 0],
  [72, 0, 75, 80, 0, 75, 72, 0],
  [70, 0, 75, 79, 0, 82, 79, 75],
];
const STAB_STEPS = [0, 3, 6, 8, 11, 14];
const ARP_ORDER = [0, 1, 2, 3, 2, 1, 3, 2, 0, 2, 1, 3, 2, 1, 3, 2];

const isIntro = (b) => b < 2;
const isBreak = (b) => b === 16;
const isOutro = (b) => b >= 21;
const isMain = (b) => !isIntro(b) && !isBreak(b) && b < 21;
const SECTION_STARTS = [2, 6, 8, 11, 13, 17, 21];
const kickTimes = [];

for (let b = 0; b < BARS; b++) {
  const chord = CHORDS[b % 4];
  const t = at(b);
  const last = b === BARS - 1;

  // Drums
  if (isMain(b) || (isOutro(b) && !last)) {
    for (let q = 0; q < 4; q++) { kick(t + q * BEAT); kickTimes.push(t + q * BEAT); }
    clap(t + BEAT, isOutro(b) ? 0.7 : 1);
    clap(t + 3 * BEAT, isOutro(b) ? 0.7 : 1);
  }
  if (isMain(b)) {
    const busy = b >= 13;
    for (let s = 0; s < 16; s++) {
      const off = s % 4 === 2;
      if (off) hat(t + s * S16, 0.9, b >= 6 && s % 8 === 6, 0.15);
      else if (busy || s % 2 === 1) hat(t + s * S16, busy ? 0.45 : 0.28, false, s % 2 ? -0.25 : 0.25);
    }
    if (SECTION_STARTS.includes(b + 1)) snareRoll(t + 3 * BEAT, t + 4 * BEAT, 0.18, 0.5);
    if (busy) { kick(t + 3.5 * BEAT, 0.7); kickTimes.push(t + 3.5 * BEAT); }
  }

  // Bass: offbeat pump with a 16th pickup.
  if (isMain(b) || (isOutro(b) && !last)) {
    for (let q = 0; q < 4; q++) bassNote(t + (q + 0.5) * BEAT, chord.root, BEAT * 0.42);
    bassNote(t + 3.75 * BEAT, chord.root + 12, S16 * 0.8, 0.7);
  }

  // Chords
  if (isIntro(b)) {
    const open = (tt) => 220 + 2600 * (((b * BAR + tt) / (2 * BAR)) ** 2);
    for (const m of chord.notes) synthNote(music, t, m, BAR * 0.98, 0.13, open, { release: 0.25, attack: 0.3, rev: 0.3, pan: (m % 3 - 1) * 0.3 });
  } else if (isBreak(b)) {
    const sweep = (tt) => 300 + 5200 * ((tt / BAR) ** 2.2);
    for (const m of chord.notes) synthNote(music, t, m, BAR * 0.98, 0.085, sweep, { release: 0.2, attack: 0.05, rev: 0.35, pan: (m % 3 - 1) * 0.3 });
  } else if (last) {
    for (const m of [41, 53, 56, 60, 67, 72]) {
      synthNote(music, t, m, BAR * 0.55, 0.075, (tt) => 400 + 3600 * Math.exp(-tt * 1.4), { release: 0.5, attack: 0.004, rev: 0.9, send: 0.25, pan: (m % 3 - 1) * 0.35 });
    }
  } else {
    const bright = b >= 17 ? 4200 : b >= 8 ? 3200 : 2500;
    for (const step of STAB_STEPS) {
      for (const m of chord.notes) {
        synthNote(music, t + step * S16, m, S16 * 1.1, 0.07, (tt) => 500 + bright * Math.exp(-tt * 16), { release: 0.07, rev: 0.25, pan: (m % 3 - 1) * 0.35 });
      }
    }
  }

  // Arp
  if (!last) {
    const level = isIntro(b) ? lerp(0.035, 0.08, (b + 0.5) / 2) : isBreak(b) ? 0.05 : 0.075;
    for (let s = 0; s < 16; s++) {
      const m = chord.notes[ARP_ORDER[s]] + 12;
      const pan = s % 2 ? 0.45 : -0.45;
      synthNote(music, t + s * S16, m, S16 * 0.5, level, (tt) => 700 + 3800 * Math.exp(-tt * 30), { release: 0.05, voices: 2, detune: 0.07, send: 0.5, pan });
    }
  }

  // Lead on the second drop
  if (b >= 17 && b < 21) {
    LEAD[b % 4].forEach((m, i) => {
      if (m) synthNote(music, t + i * BEAT / 2, m, BEAT * 0.42, 0.1, (tt) => 1200 + 5200 * Math.exp(-tt * 9), { release: 0.1, voices: 5, detune: 0.16, send: 0.45, rev: 0.3 });
    });
  }
}

riser(at(0), at(2), 0.5);
snareRoll(at(1, 2), at(2), 0.1, 0.5);
riser(at(16), at(17), 0.75);
snareRoll(at(16), at(17), 0.12, 0.6);
for (const b of SECTION_STARTS) crash(at(b), b === 2 || b === 17 || b === 21 ? 1.3 : 0.8);
impact(at(2), 0.9);
impact(at(17), 1);
impact(at(21), 1);
impact(at(23), 1.15);
kick(at(23)); kickTimes.push(at(23));

// ---- Mixdown ---------------------------------------------------------------------------------

// Sidechain: duck the music bus on every kick.
{
  kickTimes.sort((a, b) => a - b);
  let k = 0;
  for (let n = 0; n < N; n++) {
    const t = n / SR;
    while (k + 1 < kickTimes.length && kickTimes[k + 1] <= t) k++;
    const since = kickTimes.length && kickTimes[k] <= t ? t - kickTimes[k] : 1;
    const duck = 1 - 0.62 * Math.exp(-since * 9);
    music[0][n] *= duck; music[1][n] *= duck;
  }
}

function pingPong(send, time, feedback, mix) {
  const d = Math.round(time * SR);
  const out = bus();
  for (let n = 0; n < N; n++) {
    const fbL = n >= d ? out[1][n - d] : 0;
    const fbR = n >= d ? out[0][n - d] : 0;
    out[0][n] = (n >= d ? send[0][n - d] : 0) + fbL * feedback;
    out[1][n] = (n >= d * 2 ? send[1][n - d * 2] : 0) + fbR * feedback;
  }
  return [out[0].map((v) => v * mix), out[1].map((v) => v * mix)];
}

function reverb(send, mix) {
  const out = bus();
  const combs = [1557, 1617, 1491, 1422, 1277, 1356];
  const allpasses = [225, 556, 441];
  for (let ch = 0; ch < 2; ch++) {
    const acc = new Float32Array(N);
    for (const base of combs) {
      const d = base + ch * 23;
      const buf = new Float32Array(d);
      let idx = 0, lpState = 0;
      for (let n = 0; n < N; n++) {
        const y = buf[idx];
        lpState = y * 0.72 + lpState * 0.28;
        buf[idx] = send[ch][n] + lpState * 0.86;
        idx = (idx + 1) % d;
        acc[n] += y;
      }
    }
    let sig = acc;
    for (const base of allpasses) {
      const d = base + ch * 11;
      const buf = new Float32Array(d);
      const next = new Float32Array(N);
      let idx = 0;
      for (let n = 0; n < N; n++) {
        const b = buf[idx];
        const x = sig[n];
        next[n] = b - x * 0.5;
        buf[idx] = x + b * 0.5;
        idx = (idx + 1) % d;
      }
      sig = next;
    }
    for (let n = 0; n < N; n++) out[ch][n] = sig[n] * mix / combs.length;
  }
  return out;
}

const delayed = pingPong(delaySend, BEAT * 0.75, 0.42, 0.55);
const wet = reverb(reverbSend, 1.6);

const rms = (b, t0, t1) => {
  let acc = 0;
  const a = Math.round(t0 * SR), z = Math.round(t1 * SR);
  for (let n = a; n < z; n++) acc += b[0][n] ** 2 + b[1][n] ** 2;
  return (10 * Math.log10(acc / (2 * (z - a)) + 1e-12)).toFixed(1);
};
if (process.argv.includes('--stats')) {
  for (const [name, b] of Object.entries({ drums, music, fx, delayed, wet })) {
    console.log(name.padEnd(8), 'intro', rms(b, 0, at(2)), 'drop', rms(b, at(2), at(16)), 'break', rms(b, at(16), at(17)), 'dropB', rms(b, at(17), at(21)), 'dB RMS');
  }
}

// Bus balance, then a gentle tanh limiter driven just hard enough to round off the kick peaks.
const GAIN = { drums: 0.72, music: 1.9, fx: 0.9, delayed: 3.2, wet: 1.0 };
const dry = bus();
let dryPeak = 0;
for (let ch = 0; ch < 2; ch++) {
  for (let n = 0; n < N; n++) {
    const v = drums[ch][n] * GAIN.drums + music[ch][n] * GAIN.music + fx[ch][n] * GAIN.fx
      + delayed[ch][n] * GAIN.delayed + wet[ch][n] * GAIN.wet;
    dry[ch][n] = v;
    dryPeak = Math.max(dryPeak, Math.abs(v));
  }
}
const DRIVE = 1.15 / dryPeak;
const mix = bus();
let peak = 0;
for (let ch = 0; ch < 2; ch++) {
  for (let n = 0; n < N; n++) {
    const t = n / SR;
    const fadeIn = Math.min(1, t / 0.02);
    const fadeOut = clamp((BARS * BAR - t) / 1.1, 0, 1) ** 1.5;
    const v = Math.tanh(dry[ch][n] * DRIVE) * fadeIn * fadeOut;
    mix[ch][n] = v;
    peak = Math.max(peak, Math.abs(v));
  }
}

// -1.5 dBFS leaves true-peak headroom for the AAC encode.
const norm = 0.84 / peak;
const pcm = Buffer.alloc(44 + N * 4);
pcm.write('RIFF', 0); pcm.writeUInt32LE(36 + N * 4, 4); pcm.write('WAVEfmt ', 8);
pcm.writeUInt32LE(16, 16); pcm.writeUInt16LE(1, 20); pcm.writeUInt16LE(2, 22);
pcm.writeUInt32LE(SR, 24); pcm.writeUInt32LE(SR * 4, 28); pcm.writeUInt16LE(4, 32); pcm.writeUInt16LE(16, 34);
pcm.write('data', 36); pcm.writeUInt32LE(N * 4, 40);
for (let n = 0; n < N; n++) {
  pcm.writeInt16LE(Math.round(clamp(mix[0][n] * norm, -1, 1) * 32767), 44 + n * 4);
  pcm.writeInt16LE(Math.round(clamp(mix[1][n] * norm, -1, 1) * 32767), 46 + n * 4);
}

const outPath = resolve(dirname(fileURLToPath(import.meta.url)), '../out/hype-beat.wav');
mkdirSync(dirname(outPath), { recursive: true });
writeFileSync(outPath, pcm);
console.log(`Wrote ${outPath} (${(N / SR).toFixed(2)} s, ${BPM} BPM, dry peak ${dryPeak.toFixed(2)}, limited peak ${peak.toFixed(2)})`);
