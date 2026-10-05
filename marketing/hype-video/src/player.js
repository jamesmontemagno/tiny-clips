// Timeline engine for the hype video.
//
// Every motion in the piece is a CSS animation whose delay places it on one global timeline.
// Nothing ever plays on its own: all animations are paused and window.__seek(t) sets their
// currentTime, so a frame is a pure function of t. The renderer steps t at 1/fps; the preview
// drives t from the soundtrack's clock.
(() => {
  const BPM = 128;
  const BEAT = 60 / BPM;
  const BAR = BEAT * 4;
  const BARS = 24;
  const DURATION = BARS * BAR;
  const rendering = new URLSearchParams(location.search).has('render');
  const stage = document.getElementById('stage');

  document.documentElement.style.setProperty('--beat', `${BEAT}s`);
  document.documentElement.style.setProperty('--bar', `${BAR}s`);
  document.documentElement.classList.toggle('render', rendering);

  // "2" = 2 beats, "0.3s" = seconds.
  const time = (v) => (String(v).trim().endsWith('s') ? parseFloat(v) : parseFloat(v) * BEAT);

  // ---- Release content (content.js) ----------------------------------------------------------

  const problems = [];
  const ACCENTS = ['blue', 'violet', 'pink', 'orange', 'green', 'cyan', 'red'];
  const TILE_STEP = { 3: 4, 4: 3, 5: 2, 6: 2 }; // beats between tiles, so the grid is full by beat 12
  const el = (tag, cls, text) => {
    const node = document.createElement(tag);
    if (cls) node.className = cls;
    if (text !== undefined) node.textContent = text;
    return node;
  };

  // Each visual returns the element shown at the top of a tile. `at` is the tile's start in beats.
  const VISUALS = {
    compare(v) {
      const root = el('div', 'tv v-compare');
      root.style.setProperty('--ratio', Math.max(0.08, Math.min(1, v.ratio ?? 0.5)));
      for (const [label, on] of [[v.from, false], [v.to, true]]) {
        const row = el('div', on ? 'fmt on' : 'fmt');
        row.append(el('b', '', label), el('i', on ? 'size small' : 'size big'));
        root.append(row);
      }
      return root;
    },
    toggle(v) {
      const root = el('div', 'tv v-toggle');
      const seg = el('div', 'seg');
      seg.append(el('i', 'thumb'), el('span', '', v.from), el('span', 'on', v.to));
      root.append(seg);
      if (v.caption) root.append(el('em', '', v.caption));
      return root;
    },
    emoji(v) {
      const root = el('div', 'tv v-emoji');
      const [a, b, c] = v.emoji ?? [];
      const picked = el('span', 'e e2');
      picked.append(el('i', 'grip'), document.createTextNode(b ?? ''));
      root.append(el('span', 'e e1', a ?? ''), picked, el('span', 'e e3', c ?? ''));
      return root;
    },
    slider(v, at) {
      const root = el('div', 'tv v-slider');
      const from = v.from ?? 100;
      const to = v.to ?? 50;
      const max = Math.max(from, to, 1);
      const unit = v.unit ?? '%';
      root.style.setProperty('--p0', from / max);
      root.style.setProperty('--p1', to / max);
      const counter = (tag, format) => {
        const node = el(tag);
        Object.assign(node.dataset, { count: 'custom', from, to, at: at + 0.6, len: 1.6 });
        node.__format = format;
        return node;
      };
      const value = (n) => `${Math.round(n)}${unit}`;
      const slider = el('div', 'slider');
      slider.append(el('i', 'fill'), el('i', 'knob'));
      if (v.size) {
        const [w, h] = v.size;
        root.append(counter('strong', (n) => `${Math.round(w * n / max)} × ${Math.round(h * n / max)}`), slider, counter('b', value));
      } else {
        root.append(counter('strong', value), slider);
      }
      return root;
    },
    keys(v) {
      const root = el('div', 'tv v-keys');
      (v.keys ?? []).forEach((key, k) => {
        const cap = el('kbd', '', key);
        cap.style.setProperty('--k', k);
        root.append(cap);
      });
      return root;
    },
    stat(v) {
      const root = el('div', 'tv v-stat');
      root.append(el('strong', '', v.value));
      if (v.caption) root.append(el('em', '', v.caption));
      return root;
    },
    list() {
      const root = el('div', 'tv v-list');
      for (const t of ['t1', 't2', 't3']) {
        const row = el('div', 'row');
        row.append(el('i', t), el('span', 'l'), el('span', 'l s'));
        root.append(row);
      }
      return root;
    },
    panel(v) {
      const root = el('div', 'tv v-panel');
      const grid = el('div', 'grid');
      grid.append(el('i'), el('i', 'picked'), el('i'), el('i'));
      const pane = el('div', 'pane');
      const tags = el('div', 'tags');
      for (const tag of (v.tags ?? []).slice(0, 3)) tags.append(el('b', '', tag));
      pane.append(el('i', 'preview'), el('span', 'l'), tags);
      root.append(grid, pane);
      return root;
    },
  };

  {
    const content = window.HYPE_CONTENT ?? {};
    const news = content.whatsNew ?? {};
    const tiles = news.tiles ?? [];
    const lead = document.getElementById('break-lead');
    lead.textContent = content.breakdown?.lead ?? '';
    lead.dataset.split = 'a-up';
    document.getElementById('break-title').textContent = content.breakdown?.title ?? '';
    document.getElementById('new-pill').textContent = news.pill ?? '';
    document.getElementById('new-heading').textContent = news.heading ?? '';
    document.getElementById('new-platforms').textContent = news.platforms ?? '';

    if (tiles.length < 3 || tiles.length > 6) problems.push(`content.js: whatsNew.tiles needs 3 to 6 tiles, found ${tiles.length}.`);
    const shown = tiles.slice(0, 6);
    const step = TILE_STEP[shown.length] ?? 2;
    const grid = document.getElementById('new-tiles');
    const slots = document.getElementById('new-slots');
    grid.classList.add(`n${shown.length}`);
    slots.classList.add(`n${shown.length}`);
    shown.forEach((tile, i) => {
      const name = `Tile ${i + 1} (“${tile.title}”)`;
      if (!ACCENTS.includes(tile.accent)) problems.push(`${name}: accent must be one of ${ACCENTS.join(', ')}.`);
      const build = VISUALS[tile.visual?.type];
      if (!build) problems.push(`${name}: unknown visual type “${tile.visual?.type}”. Use ${Object.keys(VISUALS).join(', ')}.`);
      const article = el('article', 'tile a-tile');
      article.dataset.at = i * step;
      article.style.setProperty('--n', i);
      article.style.setProperty('--accent', `var(--${ACCENTS.includes(tile.accent) ? tile.accent : 'blue'})`);
      article.append(build ? build(tile.visual, i * step) : el('div', 'tv'), el('h3', '', tile.title), el('p', '', tile.blurb));
      grid.append(article);
      slots.append(el('i'));
    });
  }

  // ---- Templates -----------------------------------------------------------------------------

  const MOCK_APP = `
    <div class="win-bar"><i></i><i></i><i></i><span></span></div>
    <div class="win-body">
      <aside><b></b><span></span><span></span><span></span><span></span><span></span></aside>
      <main>
        <div class="m-head"><span class="l" style="width:38%"></span><em></em></div>
        <div class="m-cards">
          <div><span class="l" style="width:55%"></span><strong style="--c:var(--cyan)"></strong></div>
          <div><span class="l" style="width:70%"></span><strong style="--c:var(--pink)"></strong></div>
          <div><span class="l" style="width:45%"></span><strong style="--c:var(--green)"></strong></div>
        </div>
        <div class="m-chart">${[38, 62, 45, 80, 56, 92, 70, 48, 86, 64, 76, 52].map((h) => `<i style="--h:${h}%"></i>`).join('')}</div>
        <div class="m-lines"><span class="l" style="width:86%"></span><span class="l" style="width:64%"></span></div>
      </main>
    </div>`;
  for (const el of document.querySelectorAll('[data-mock="app"]')) el.innerHTML = MOCK_APP;

  // A capture card shows the same mock, offset so it lines up with the region that was selected.
  for (const el of document.querySelectorAll('[data-crop]')) {
    const [x, y, w, h] = el.dataset.crop.split(',').map(Number);
    const inner = document.createElement('div');
    inner.className = 'win';
    inner.innerHTML = MOCK_APP;
    Object.assign(inner.style, { position: 'absolute', left: `${-x}px`, top: `${-y}px`, width: `${w}px`, height: `${h}px` });
    el.appendChild(inner);
  }

  for (const el of document.querySelectorAll('[data-repeat]')) {
    el.innerHTML = Array.from({ length: Number(el.dataset.repeat) }, (_, i) => el.innerHTML.replaceAll('{i}', i)).join('');
  }

  // ---- Timing attributes -> CSS variables ----------------------------------------------------

  for (const el of document.querySelectorAll('[data-bar]')) {
    el.style.setProperty('--start', `${parseFloat(el.dataset.bar) * BAR}s`);
    if (el.dataset.bars) el.style.setProperty('--dur', `${parseFloat(el.dataset.bars) * BAR}s`);
  }

  for (const el of document.querySelectorAll('[data-split]')) {
    const stagger = el.dataset.stagger ?? '0.03s';
    const cls = el.dataset.split;
    const words = el.textContent.trim().split(/\s+/);
    el.textContent = '';
    let i = 0;
    words.forEach((word, w) => {
      const wrap = document.createElement('span');
      wrap.className = 'word';
      for (const ch of Array.from(word)) {
        const span = document.createElement('span');
        span.className = `ch ${cls}`;
        span.textContent = ch;
        span.style.setProperty('--i', i++);
        span.style.setProperty('--stagger', stagger);
        wrap.appendChild(span);
      }
      el.appendChild(wrap);
      if (w < words.length - 1) el.appendChild(document.createTextNode(' '));
    });
  }

  for (const el of document.querySelectorAll('[data-at]')) el.style.setProperty('--at', `${time(el.dataset.at)}s`);
  for (const el of document.querySelectorAll('[data-d]')) el.style.setProperty('--d', `${time(el.dataset.d)}s`);
  for (const el of document.querySelectorAll('[data-stagger-children]')) {
    const step = time(el.dataset.staggerChildren);
    const base = el.dataset.at ? time(el.dataset.at) : 0;
    [...el.children].forEach((child, i) => child.style.setProperty('--at', `${base + i * step}s`));
  }

  // A flash on every cut; bigger on the drops.
  const flashes = document.getElementById('flashes');
  const DROPS = new Set([2, 17, 21]);
  for (const scene of document.querySelectorAll('.scene')) {
    const bar = parseFloat(scene.dataset.bar);
    if (bar === 0) continue;
    const flash = document.createElement('i');
    flash.style.setProperty('--start', `${bar * BAR}s`);
    flash.style.setProperty('--peak', DROPS.has(bar) ? 0.55 : 0.14);
    flashes.appendChild(flash);
  }

  // Film grain hides banding in the dark gradients once the video is compressed.
  const grain = document.getElementById('grain');
  {
    const c = document.createElement('canvas');
    c.width = c.height = 256;
    const ctx = c.getContext('2d');
    const img = ctx.createImageData(256, 256);
    let s = 1234567;
    for (let i = 0; i < img.data.length; i += 4) {
      s = (s * 1664525 + 1013904223) >>> 0;
      const v = s >>> 24;
      img.data[i] = img.data[i + 1] = img.data[i + 2] = v;
      img.data[i + 3] = 255;
    }
    ctx.putImageData(img, 0, 0);
    grain.style.backgroundImage = `url(${c.toDataURL()})`;
  }

  // ---- Numeric readouts ----------------------------------------------------------------------

  const FORMATS = {
    timer: (v) => `${Math.floor(v / 60)}:${String(Math.floor(v % 60)).padStart(2, '0')}.${Math.floor((v * 10) % 10)}`,
    dur: (v) => `0:${String(Math.floor(v)).padStart(2, '0')}.${Math.floor((v * 10) % 10)}`,
    sq: (v) => `${Math.round(v)} × ${Math.round(v)}`,
  };
  const EASES = {
    linear: (p) => p,
    out: (p) => 1 - (1 - p) ** 3,
    inout: (p) => (p < 0.4 ? 3.9 * p ** 3 : 1 - ((1 - p) ** 2.6) * 2.83),
  };
  const counters = [...document.querySelectorAll('[data-count]')].map((el) => {
    const scene = el.closest('[data-bar]');
    const t0 = parseFloat(scene.dataset.bar) * BAR + time(el.dataset.at ?? 0);
    return {
      el, t0,
      len: time(el.dataset.len ?? 4),
      from: parseFloat(el.dataset.from ?? 0),
      to: parseFloat(el.dataset.to ?? 1),
      format: el.__format ?? FORMATS[el.dataset.count],
      ease: EASES[el.dataset.ease ?? 'out'],
    };
  });

  // ---- Seeking -------------------------------------------------------------------------------

  let animations = [];
  function seek(t) {
    const clamped = Math.max(0, Math.min(DURATION - 1e-4, t));
    const ms = clamped * 1000;
    for (const a of animations) a.currentTime = ms;
    for (const c of counters) {
      const p = Math.max(0, Math.min(1, (clamped - c.t0) / c.len));
      c.el.textContent = c.format(c.from + (c.to - c.from) * c.ease(p));
    }
    const frame = Math.round(clamped * 60);
    grain.style.backgroundPosition = `${(frame * 89) % 256}px ${(frame * 151) % 256}px`;
    return clamped;
  }

  async function init() {
    await document.fonts.ready;
    await Promise.all([...document.images].map((img) => img.decode().catch(() => {})));
    fitBreakTitle();
    animations = document.getAnimations();
    for (const a of animations) a.pause();
    validate();
    seek(0);
    window.__duration = DURATION;
    window.__seek = seek;
    window.__problems = problems;
    window.__reviewTimes = reviewTimes();
    if (!rendering) preview();
    window.__ready = true;
  }

  // ---- Fitting and checks --------------------------------------------------------------------

  // The breakdown title grows to 112% and must stay inside the frame at any length.
  function fitBreakTitle() {
    const title = document.getElementById('break-title');
    const probe = title.cloneNode(true);
    probe.removeAttribute('id');
    probe.style.cssText = 'position:absolute;left:auto;right:auto;top:0;visibility:hidden;animation:none;letter-spacing:-0.045em;';
    title.parentNode.appendChild(probe);
    const base = parseFloat(getComputedStyle(probe).fontSize);
    const widest = probe.offsetWidth * 1.12;
    probe.remove();
    if (widest > 1780) title.style.fontSize = `${Math.floor(base * 1780 / widest)}px`;
  }

  function lineCount(node) {
    const range = document.createRange();
    range.selectNodeContents(node);
    return new Set([...range.getClientRects()].map((r) => Math.round(r.top))).size;
  }

  function validate() {
    seek(20.99 * BAR); // every tile has landed and settled
    document.querySelectorAll('#new-tiles .tile').forEach((tile, i) => {
      const title = tile.querySelector('h3');
      const name = `Tile ${i + 1} (“${title.textContent}”)`;
      if (lineCount(title) > 1) problems.push(`${name}: the title wraps. Shorten it to fit one line.`);
      if (lineCount(tile.querySelector('p')) > 2) problems.push(`${name}: the blurb runs past two lines. Shorten it.`);
      // The tile clips its contents, so anything reaching the edge would be cut off.
      const box = tile.getBoundingClientRect();
      const clipped = [...tile.querySelectorAll('.tv *')].some((node) => {
        const r = node.getBoundingClientRect();
        return r.width > 0 && (r.left < box.left + 6 || r.right > box.right - 6);
      });
      if (clipped) problems.push(`${name}: the visual is wider than the tile. Use shorter labels or fewer items.`);
    });
    const heading = document.getElementById('new-heading').getBoundingClientRect();
    const platforms = document.getElementById('new-platforms').getBoundingClientRect();
    if (heading.right > platforms.left - 24 || lineCount(document.getElementById('new-heading')) > 1) {
      problems.push('whatsNew.heading is too long for the header. Shorten it.');
    }
  }

  // One frame late in every scene, plus one as each "what's new" tile lands.
  function reviewTimes() {
    const times = [...document.querySelectorAll('.scene')].map((scene) => (parseFloat(scene.dataset.bar) + parseFloat(scene.dataset.bars) * 0.85) * BAR);
    for (const tile of document.querySelectorAll('#new-tiles .tile')) times.push(17 * BAR + (parseFloat(tile.dataset.at) + 1.5) * BEAT);
    times.push(DURATION - 0.05);
    return [...new Set(times.map((t) => Math.round(t * 100) / 100))].sort((a, b) => a - b);
  }

  // ---- Preview UI ----------------------------------------------------------------------------

  function preview() {
    if (problems.length) {
      const banner = document.createElement('div');
      banner.id = 'problems';
      banner.textContent = problems.join('\n');
      document.body.appendChild(banner);
    }
    const fit = () => {
      const scale = Math.min(innerWidth / 1920, (innerHeight - 56) / 1080);
      stage.style.transform = `scale(${scale})`;
      stage.style.left = `${(innerWidth - 1920 * scale) / 2}px`;
    };
    addEventListener('resize', fit);
    fit();

    const bar = document.createElement('div');
    bar.id = 'transport';
    bar.innerHTML = '<button type="button" aria-label="Play or pause">Play</button><input type="range" min="0" max="1" step="0.0005" value="0" aria-label="Scrub"><output></output>';
    document.body.appendChild(bar);
    const [button, range, readout] = bar.children;
    const audio = new Audio('../out/hype-beat.wav');
    let hasAudio = false;
    audio.addEventListener('canplaythrough', () => { hasAudio = true; }, { once: true });

    let t = 0;
    let playing = false;
    let last = 0;
    const show = () => {
      t = seek(t);
      range.value = t / DURATION;
      readout.textContent = `${t.toFixed(2)}s · bar ${(t / BAR).toFixed(2)}`;
    };
    const tick = (now) => {
      if (!playing) return;
      t = hasAudio ? audio.currentTime : t + (now - last) / 1000;
      last = now;
      if (t >= DURATION - 0.02) { toggle(false); t = DURATION; }
      show();
      requestAnimationFrame(tick);
    };
    const toggle = (on = !playing) => {
      playing = on;
      button.textContent = playing ? 'Pause' : 'Play';
      if (playing) {
        if (t >= DURATION - 0.05) t = 0;
        if (hasAudio) { audio.currentTime = t; audio.play(); }
        last = performance.now();
        requestAnimationFrame(tick);
      } else {
        audio.pause();
      }
    };
    const jump = (to) => { t = to; if (hasAudio) audio.currentTime = Math.min(t, DURATION - 0.01); show(); };
    button.addEventListener('click', () => toggle());
    range.addEventListener('input', () => jump(range.value * DURATION));
    addEventListener('keydown', (e) => {
      if (e.code === 'Space') { e.preventDefault(); toggle(); }
      if (e.code === 'ArrowRight') jump(t + BEAT);
      if (e.code === 'ArrowLeft') jump(t - BEAT);
    });
    const initial = parseFloat(new URLSearchParams(location.search).get('t'));
    if (!Number.isNaN(initial)) t = initial;
    show();
  }

  init();
})();
