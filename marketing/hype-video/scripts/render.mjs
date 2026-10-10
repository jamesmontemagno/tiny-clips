// Renders the composition to MP4 by stepping a headless Chromium browser frame by frame.
//
//   node scripts/render.mjs                    full render -> out/tiny-clips-hype.mp4
//   node scripts/render.mjs --review           one frame per scene as contact sheets -> out/review/
//   node scripts/render.mjs --still 5,12.5     PNG stills at the given seconds -> out/stills/
//   node scripts/render.mjs --from 15 --to 20  partial render (no audio offset handling needed)
//
// Options: --fps 60  --workers <n>  --out <file>  --no-audio
//          --draft   quick look: 30 fps, JPEG frame capture, faster encode
// Set BROWSER_PATH to use a specific Chromium-based browser.
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { once } from 'node:events';
import { cpus } from 'node:os';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import puppeteer from 'puppeteer-core';
import { compositionPath, projectDir, startServer } from './static-server.mjs';

const args = process.argv.slice(2);
const flag = (name) => args.includes(`--${name}`);
const opt = (name, fallback) => {
  const i = args.indexOf(`--${name}`);
  return i >= 0 && args[i + 1] !== undefined ? args[i + 1] : fallback;
};

const WIDTH = 1920;
const HEIGHT = 1080;
const configSandbox = { window: {} };
runInNewContext(readFileSync(new URL('../src/creative.js', import.meta.url), 'utf8'), configSandbox);
const musicStyle = configSandbox.window.HYPE_CREATIVE?.musicStyle ?? 'electronic';
const draft = flag('draft');
const fps = Number(opt('fps', draft ? 30 : 60));
const workers = Number(opt('workers', Math.max(2, Math.min(6, Math.floor(cpus().length / 2)))));
const outDir = join(projectDir, 'out');
const outFile = opt('out', join(outDir, draft ? 'tiny-clips-hype-draft.mp4' : 'tiny-clips-hype.mp4'));
const audioFile = join(outDir, 'hype-beat.wav');

function findBrowser() {
  const candidates = [
    process.env.BROWSER_PATH,
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Chromium.app/Contents/MacOS/Chromium',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
    '/usr/bin/microsoft-edge',
  ].filter(Boolean);
  const found = candidates.find((p) => existsSync(p));
  if (!found) throw new Error('No Chromium-based browser found. Set BROWSER_PATH to Edge or Chrome.');
  return found;
}

function run(cmd, cmdArgs) {
  return new Promise((done, fail) => {
    const child = spawn(cmd, cmdArgs, { stdio: ['ignore', 'inherit', 'inherit'] });
    child.on('error', fail);
    child.on('exit', (code) => (code === 0 ? done() : fail(new Error(`${cmd} exited with ${code}`))));
  });
}

const frameFormat = draft
  ? { format: 'jpeg', quality: 95, optimizeForSpeed: true }
  : { format: 'png', optimizeForSpeed: true };

class ContentError extends Error {
  constructor(problems) {
    super(`The composition has ${problems.length} problem(s):\n${problems.map((p) => `  - ${p}`).join('\n')}`);
    this.name = 'ContentError';
  }
}

const launch = () => puppeteer.launch({
  executablePath: findBrowser(),
  headless: true,
  args: [
    '--hide-scrollbars',
    '--force-color-profile=srgb',
    '--disable-background-timer-throttling',
    '--disable-renderer-backgrounding',
    '--disable-backgrounding-occluded-windows',
    '--font-render-hinting=none',
  ],
});

// One browser per worker: screenshots within a single browser are serialized.
async function openComposition(url) {
  const browser = await launch();
  try {
    const page = await browser.newPage();
    await page.setViewport({ width: WIDTH, height: HEIGHT, deviceScaleFactor: 1 });
    page.on('pageerror', (err) => console.error('[page error]', err.message));
    page.on('console', (msg) => { if (msg.type() === 'error') console.error('[page]', msg.text()); });
    await page.goto(url, { waitUntil: 'networkidle0' });
    await page.waitForFunction('window.__ready === true', { timeout: 30000 });
    const problems = await page.evaluate(() => window.__problems);
    if (problems.length) throw new ContentError(problems);
    const cdp = await page.createCDPSession();
    return {
      page,
      seek: (t) => page.evaluate((time) => window.__seek(time), t),
      capture: async () => {
        const { data } = await cdp.send('Page.captureScreenshot', frameFormat);
        return Buffer.from(data, 'base64');
      },
      close: () => browser.close(),
    };
  } catch (err) {
    await browser.close();
    throw err;
  }
}

const { server, port } = await startServer();
const url = `http://127.0.0.1:${port}${compositionPath}?render`;
try {
  mkdirSync(outDir, { recursive: true });

  if (flag('still')) {
    const comp = await openComposition(url);
    try {
      const stillDir = join(outDir, 'stills');
      mkdirSync(stillDir, { recursive: true });
      for (const raw of String(opt('still', '0')).split(',')) {
        const t = Number(raw);
        await comp.seek(t);
        const file = join(stillDir, `t${t.toFixed(2).padStart(5, '0')}.png`);
        writeFileSync(file, await comp.capture());
        console.log(file);
      }
    } finally {
      await comp.close();
    }
  } else if (flag('review')) {
    // One frame per scene (and per "what's new" tile), tiled into contact sheets to look over.
    const comp = await openComposition(url);
    const reviewDir = join(outDir, 'review');
    rmSync(reviewDir, { recursive: true, force: true });
    mkdirSync(reviewDir, { recursive: true });
    try {
      const times = await comp.page.evaluate(() => window.__reviewTimes);
      for (const [i, t] of times.entries()) {
        await comp.seek(t);
        writeFileSync(join(reviewDir, `frame-${String(i).padStart(3, '0')}.png`), await comp.capture());
      }
      const perSheet = 9;
      for (let n = 0; n * perSheet < times.length; n++) {
        const sheet = join(reviewDir, `sheet-${n + 1}.png`);
        await run('ffmpeg', [
          '-hide_banner', '-loglevel', 'error', '-y',
          '-start_number', String(n * perSheet), '-i', join(reviewDir, 'frame-%03d.png'),
          '-frames:v', '1', '-vf', 'scale=640:360,tile=3x3', sheet,
        ]);
        const shown = times.slice(n * perSheet, (n + 1) * perSheet).map((t) => `${t.toFixed(1)}s`).join('  ');
        console.log(`${sheet}\n    ${shown}`);
      }
    } finally {
      await comp.close();
    }
  } else {
    const probe = await openComposition(url);
    const duration = await probe.page.evaluate(() => window.__duration);
    await probe.close();

    const from = Number(opt('from', 0));
    const to = Math.min(Number(opt('to', duration)), duration);
    const first = Math.round(from * fps);
    const total = Math.round(to * fps) - first;
    const count = Math.max(1, Math.min(workers, total));
    const chunkDir = join(outDir, 'chunks');
    rmSync(chunkDir, { recursive: true, force: true });
    mkdirSync(chunkDir, { recursive: true });

    let done = 0;
    const started = Date.now();
    const report = setInterval(() => {
      const rate = done / ((Date.now() - started) / 1000);
      process.stdout.write(`\r${done}/${total} frames  ${rate.toFixed(1)} fps  `);
    }, 1000);

    const chunks = await Promise.all(Array.from({ length: count }, async (_, w) => {
      const a = first + Math.floor((total * w) / count);
      const b = first + Math.floor((total * (w + 1)) / count);
      const file = join(chunkDir, `chunk-${String(w).padStart(2, '0')}.mp4`);
      const ffmpeg = spawn('ffmpeg', [
        '-hide_banner', '-loglevel', 'error', '-y',
        '-f', 'image2pipe', '-c:v', draft ? 'mjpeg' : 'png', '-framerate', String(fps), '-i', '-',
        '-vf', 'scale=in_range=pc:out_range=tv:out_color_matrix=bt709,format=yuv420p',
        '-c:v', 'libx264', '-preset', draft ? 'veryfast' : 'medium', '-crf', draft ? '21' : '18', '-g', String(fps), '-bf', '2',
        '-colorspace', 'bt709', '-color_primaries', 'bt709', '-color_trc', 'bt709', '-color_range', 'tv',
        '-r', String(fps), file,
      ], { stdio: ['pipe', 'inherit', 'inherit'] });
      const exited = once(ffmpeg, 'exit');
      const comp = await openComposition(url);
      try {
        for (let f = a; f < b; f++) {
          await comp.seek(f / fps);
          if (!ffmpeg.stdin.write(await comp.capture())) await once(ffmpeg.stdin, 'drain');
          done++;
        }
      } finally {
        ffmpeg.stdin.end();
        await comp.close();
      }
      const [code] = await exited;
      if (code !== 0) throw new Error(`ffmpeg chunk ${w} exited with ${code}`);
      return file;
    }));

    clearInterval(report);
    console.log(`\rRendered ${total} frames in ${((Date.now() - started) / 1000).toFixed(0)} s`);

    const list = join(chunkDir, 'chunks.txt');
    writeFileSync(list, chunks.map((f) => `file '${f.replaceAll("'", "'\\''")}'`).join('\n'));
    const withAudio = !flag('no-audio') && musicStyle !== 'silent' && existsSync(audioFile);
    if (!withAudio && !flag('no-audio') && musicStyle !== 'silent') console.warn('No soundtrack found (run "npm run audio"); rendering silent.');
    await run('ffmpeg', [
      '-hide_banner', '-loglevel', 'error', '-y',
      '-f', 'concat', '-safe', '0', '-i', list,
      ...(withAudio ? ['-ss', String(from), '-t', String(to - from), '-i', audioFile] : []),
      '-map', '0:v', '-c:v', 'copy',
      ...(withAudio ? ['-map', '1:a', '-c:a', 'aac', '-b:a', '256k', '-ar', '48000'] : []),
      '-movflags', '+faststart', outFile,
    ]);
    rmSync(chunkDir, { recursive: true, force: true });
    console.log(outFile);
  }
} catch (err) {
  console.error(err instanceof ContentError ? err.message : err);
  process.exitCode = 1;
} finally {
  server.close();
}
// Browser helper processes can hold the launch pipes open long after the browser has closed,
// which would keep this process alive with nothing left to do.
process.exit();
