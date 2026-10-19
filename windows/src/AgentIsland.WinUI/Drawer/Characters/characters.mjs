// Agent Island drawer characters.
//
// One pixel bot per agent, after Clawd, Claude Code's own pixel mascot, each
// drawn from the idea behind that agent's icon: Clawd, the Codex notebook,
// Antigravity's balloon, Grok's ring head (which Cursor shares), Kimi Code's
// blob and terminal, DeepSeek's whale. Every live phase has its own motion.
//
// Pure JS, no DOM: the drawer runs it in embedded V8, the preview page in a
// browser. Frames are SVG in the subset the drawer's Win2D renderer draws:
// g with a translate, and paths of axis-aligned rectangles (M H V Z) with a
// fill and an opacity.

export const KINDS = ['claude', 'codex', 'antigravity', 'grok', 'cursor', 'kimi', 'deepseek'];

export const PHASES = [
  'idle', 'thinking', 'streaming', 'reading', 'searching', 'web', 'writing', 'running',
  'controlling', 'spawning', 'orbit', 'sending', 'asking', 'approving', 'compacting',
  'working', 'finished', 'listening', 'failed', 'broken', 'offline', 'sleeping', 'locked',
  'resting',
];

// States slow enough for the drawer's calm frame rate.
export const CALM = ['idle', 'listening', 'broken', 'offline', 'sleeping', 'locked', 'resting'];

// One-shot entries: how long each plays and the state it settles into.
const SETTLE = { finished: ['listening', 1.7], failed: ['broken', 1.5] };

const BLEND = 0.32;
const RIM = '#FFFFFF';

// MARK: - Math

const PI = Math.PI;
const TAU = PI * 2;
const rad = d => d * PI / 180;
const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
const lerp = (a, b, t) => a + (b - a) * t;
const frac = v => v - Math.floor(v);
const smooth = t => { t = clamp(t); return t * t * (3 - 2 * t); };
const easeInOut = t => { t = clamp(t); return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2; };
const bump = t => (t <= 0 || t >= 1) ? 0 : Math.sin(PI * t);
const hash = i => frac(Math.sin(i * 127.1 + 311.7) * 43758.5453);
const angleDelta = (a, b) => ((b - a) % 360 + 540) % 360 - 180;
// A damped spring kick, 0 at rest.
const kick = (q, freq = 15, decay = 5.5) => (q < 0 || q > 1.6) ? 0 : Math.sin(q * freq) * Math.exp(-q * decay);

function rng(seed) {
  let s = (seed >>> 0) || 1;
  return () => {
    s = (s + 0x6D2B79F5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

// MARK: - SVG

const n = v => {
  const r = Math.round(v * 100) / 100;
  return r === 0 ? '0' : String(r);
};
const M = (x, y) => `M${n(x)} ${n(y)}`;
const path = (d, a = '') => d ? `<path d="${d}"${a}/>` : '';
const group = (a, inner) => inner ? (a ? `<g${a}>${inner}</g>` : inner) : '';
const F = c => ` fill="${c}"`;
const O = o => o >= 0.995 ? '' : ` opacity="${n(clamp(o))}"`;
const TF = (...parts) => {
  const s = parts.filter(Boolean).join(' ');
  return s ? ` transform="${s}"` : '';
};
const tr = (x, y) => (Math.abs(x) < 0.005 && Math.abs(y) < 0.005) ? '' : `translate(${n(x)} ${n(y)})`;

const hexRgb = h => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const rgbHex = rgb => '#' + rgb.map(v => Math.round(clamp(v, 0, 255)).toString(16).padStart(2, '0')).join('');
const mixHex = (a, b, t) => t <= 0.002 ? a : t >= 0.998 ? b : rgbHex(hexRgb(a).map((v, i) => lerp(v, hexRgb(b)[i], t)));

// MARK: - Engine

const CHARACTERS = {};

// Expression channels every pose carries; the pixel eyes read them.
const EYE_KEYS = ['open', 'happy', 'cross', 'sad', 'wide', 'sleep', 'wink', 'squint'];
const REST_EYES = { look: [0, 0], gazeW: 1, blink: 1, open: 1, happy: 0, cross: 0, sad: 0, wide: 0, sleep: 0, wink: 0, squint: 0, stir: 0 };
// A dozing bot (a pose with stir: 1, out of quota) keeps its eyes closed; it half-opens
// them and looks when the pointer comes within WAKE DIP, and a poke opens
// them for STIR seconds.
const STIR = 1.4;
const WAKE = 120;

// Numbers blend; anything else (a limb pose, a prop name) switches halfway.
function mixPoses(a, b, w, angles) {
  const out = {};
  for (const key in b) {
    const va = a[key], vb = b[key];
    if (typeof vb === 'number' && typeof va === 'number') {
      out[key] = angles.includes(key) ? va + angleDelta(va, vb) * w : lerp(va, vb, w);
    } else if (Array.isArray(vb) && Array.isArray(va) && va.length === vb.length) {
      out[key] = vb.map((v, i) => typeof v === 'number' ? lerp(va[i], v, w) : v);
    } else {
      out[key] = w < 0.5 && va !== undefined ? va : vb;
    }
  }
  return out;
}

export function createBot(kind, { seed = 1, tempo = 1, rim = true } = {}) {
  const def = CHARACTERS[kind] ?? CHARACTERS.claude;
  const random = rng(seed);
  const k = { kind, seed, r: Array.from({ length: 24 }, random) };
  const rest = { ...REST_EYES, ...def.rest };
  const angles = def.angles ?? [];
  let T = 40 + random() * 300;
  let cur = { state: 'idle', t: random() * 30 };
  let from = null;
  let w = 1;
  let gaze = null;
  const look = [0, 0];
  let nextBlink = T + 0.8 + random() * 3;
  let blinkAt = -99;
  let pokeAt = -99;
  let rimWidth = rim ? 0.9 : 0;
  let pose = null;

  const poseOf = s => ({ ...rest, ...def.pose(s.state, s.t, T, k) });

  const blended = () => {
    const b = poseOf(cur);
    if (w >= 1 || !from) return b;
    const a = from.pose ?? poseOf(from.live);
    return mixPoses(a, b, easeInOut(w), angles);
  };

  const bot = {
    kind,
    get state() { return cur.state; },
    get calm() { return CALM.includes(cur.state); },
    fidgets: def.fidgets,
    setState(state, phase = -1) {
      if (state === cur.state && phase < 0) return;
      from = w < 1 ? { pose: blended() } : { live: { ...cur } };
      w = 0;
      const cycle = def.cycle?.[state] ?? 6;
      cur = { state, t: phase >= 0 ? phase * cycle : random() * cycle };
    },
    setGaze(dx, dy) { gaze = [dx, dy]; },
    clearGaze() { gaze = null; },
    setRim(on) { rimWidth = on ? 0.9 : 0; },
    poke() { pokeAt = T; },
    advance(dt) {
      dt = Math.max(0, Math.min(dt, 0.25)) * tempo;
      T += dt;
      cur.t += dt;
      if (from?.live) from.live.t += dt;
      if (w < 1) w = Math.min(1, w + dt / BLEND);
      if (w >= 1) from = null;
      const settle = SETTLE[cur.state];
      if (settle && cur.t >= settle[1]) bot.setState(settle[0], 0);
      if (T >= nextBlink) {
        blinkAt = T;
        nextBlink = T + (random() < 0.16 ? 0.32 : 2.4 + random() * 4.4);
      }
      pose = blended();
      let target = pose.look;
      if (gaze) {
        const sat = v => v / Math.sqrt(1 + v * v);
        const g = [sat(gaze[0] / 70), sat(gaze[1] / 70)];
        const resting = pose.stir > 0.5;
        const gw = resting && T - pokeAt < STIR ? 1 : resting && Math.hypot(gaze[0], gaze[1]) >= WAKE ? 0 : pose.gazeW;
        target = [lerp(target[0], g[0], gw), lerp(target[1], g[1], gw)];
      }
      const follow = 1 - Math.exp(-dt * 11);
      look[0] += (target[0] - look[0]) * follow;
      look[1] += (target[1] - look[1]) * follow;
    },
    svg(size = 44, id = 'b') {
      if (!pose) pose = blended();
      // A poke stirs a resting bot: its eyes open for a moment, then it
      // settles again.
      const close = gaze && Math.hypot(gaze[0], gaze[1]) < WAKE;
      const shown = pose.stir > 0.5 && T - pokeAt < STIR ? { ...pose, eyes: 'open', sleep: 0, blink: 1 }
        : pose.stir > 0.5 && close ? { ...pose, eyes: 'rest' } : pose;
      const q = T - blinkAt;
      const blink = shown.blink > 0.5 && q < 0.17 ? bump(q / 0.17) : 0;
      const eye = { look: [look[0], look[1]] };
      for (const key of EYE_KEYS) eye[key] = shown[key];
      eye.open = shown.open * (1 - blink);
      const c = { T, id, rim: rimWidth, eye, k };
      const body = def.draw(shown, c);
      // A click makes the character hop a whole pixel (2 DIP), so pixel art
      // stays on its grid.
      const p = T - pokeAt;
      const hop = kick(p);
      const wrap = TF(tr(0, -2 * Math.round(Math.max(0, hop) * 1.4)));
      return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 44 44" width="${n(size)}" height="${n(size)}">${group(wrap, body)}</svg>`;
    },
  };
  pose = blended();
  return bot;
}

export const fidgetsOf = kind => (CHARACTERS[kind] ?? CHARACTERS.claude).fidgets;
export const cycleOf = (kind, state) => (CHARACTERS[kind] ?? CHARACTERS.claude).cycle?.[state] ?? 3;
export const settleOf = state => SETTLE[state] ?? null;
export const swatchOf = kind => (CHARACTERS[kind] ?? CHARACTERS.claude).swatch;
export const nameOf = kind => (CHARACTERS[kind] ?? CHARACTERS.claude).name;

// MARK: - Pixel kit
//
// Every character is pixel art on the same 22 × 22 grid of 2-DIP cells,
// moving a whole pixel at a time, after Clawd, Claude Code's own mascot.
// This kit holds what they share: the canvas and its renderer, slit eyes,
// a base pose for every phase and the props most phases use. Each character
// brings its body, its palette and the signature props that are its own.

const PX = 2;
const GRID = 22;
const rnd = Math.round;
const steps = (t, fps) => Math.floor(t * fps) / fps;
// Integer steps from a to b as u runs 0..1: a walk, one pixel at a time.
const stepsTo = (u, a, b) => Math.round(lerp(a, b, clamp(u)));

// A pixel canvas: cells keyed by column and row; later paint wins.
function canvas() {
  const cells = new Map();
  const put = (x, y, color) => {
    if (x < -2 || x > GRID + 1 || y < -2 || y > GRID + 1 || !color) return;
    cells.set(`${x},${y}`, color);
  };
  const cv = {
    cells,
    put,
    has: (x, y) => cells.has(`${x},${y}`),
    get: (x, y) => cells.get(`${x},${y}`),
    clear: (x, y) => cells.delete(`${x},${y}`),
    rect(x, y, w, h, color) {
      for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) put(x + i, y + j, color);
    },
    // Rows of characters: '#' takes `color`, '.' is empty, other letters map.
    glyph(rows, x, y, color, map = {}, flip = false) {
      rows.forEach((row, j) => [...row].forEach((ch, i) => {
        const cx = flip ? x + row.length - 1 - i : x + i;
        if (ch === '#') put(cx, y + j, color);
        else if (ch !== '.') put(cx, y + j, map[ch] ?? '#FFFFFF');
      }));
    },
    // Every cell whose centre passes `inside(x, y)` within the box.
    shape(inside, x0, y0, w, h, color) {
      const cellsIn = [];
      for (let y = y0; y < y0 + h; y++) for (let x = x0; x < x0 + w; x++) {
        if (inside(x + 0.5, y + 0.5)) { put(x, y, typeof color === 'function' ? color(x, y) : color); cellsIn.push([x, y]); }
      }
      return cellsIn;
    },
    // A darker line just outside the given cells, for pale bodies on pale walls.
    outline(list, color) {
      const mine = new Set(list.map(([x, y]) => `${x},${y}`));
      for (const [x, y] of list) {
        for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
          const k = `${x + dx},${y + dy}`;
          if (!mine.has(k) && !cells.has(k)) put(x + dx, y + dy, color);
        }
      }
    },
  };
  return cv;
}

// Cells to SVG: an optional white sticker outline, then one path per colour
// with each row's runs merged.
function pixels(cv, c, opacity = 1) {
  const byColor = new Map();
  for (const [key, color] of cv.cells) {
    if (!byColor.has(color)) byColor.set(color, []);
    byColor.get(color).push(key.split(',').map(Number));
  }
  const runs = list => {
    const rows = new Map();
    for (const [x, y] of list) {
      if (!rows.has(y)) rows.set(y, []);
      rows.get(y).push(x);
    }
    let d = '';
    for (const [y, xs] of rows) {
      xs.sort((a, b) => a - b);
      let start = xs[0], prev = xs[0];
      for (let i = 1; i <= xs.length; i++) {
        if (i < xs.length && xs[i] === prev + 1) { prev = xs[i]; continue; }
        d += `${M(start * PX, y * PX)}H${n((prev + 1) * PX)}V${n((y + 1) * PX)}H${n(start * PX)}Z`;
        start = prev = xs[i];
      }
    }
    return d;
  };
  let out = '';
  if (c.rim) {
    const ring = new Map();
    for (const key of cv.cells.keys()) {
      const [x, y] = key.split(',').map(Number);
      for (const [dx, dy] of [[1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [-1, -1], [1, -1], [-1, 1]]) {
        const k = `${x + dx},${y + dy}`;
        if (!cv.cells.has(k)) ring.set(k, [x + dx, y + dy]);
      }
    }
    out += path(runs([...ring.values()]), F(RIM) + O(0.92));
  }
  for (const [color, list] of byColor) out += path(runs(list), F(color));
  return group(O(opacity), out);
}

// MARK: - The drawer's circle

// The drawer shows each bot only inside its 44-DIP box's inscribed circle,
// 22 DIP from the centre, white rim included (the user's call, 2026-10-15).
// SAFE marks the cells that may hold paint: the cell and its whole rim ring
// fall inside the circle.
const SAFE = (() => {
  const far = v => (v <= 10 ? 11 - v : v - 10);
  const inside = (x, y) => x >= 0 && y >= 0 && x < GRID && y < GRID && far(x) ** 2 + far(y) ** 2 <= 121;
  return Array.from({ length: GRID }, (_, y) => Array.from({ length: GRID }, (_, x) =>
    [-1, 0, 1].every(i => [-1, 0, 1].every(j => inside(x + i, y + j)))));
})();
const safe = (x, y) => SAFE[y]?.[x] === true;
const allSafe = cv => [...cv.cells.keys()].every(k => { const [x, y] = k.split(',').map(Number); return safe(x, y); });

// The smallest whole-cell shift that brings every cell inside, preferring
// sideways steps toward the middle, and a spot clear of `clear` (the body)
// before any spot at all.
function fitShift(cells, clear) {
  const fits = (dx, dy) => cells.every(([x, y]) => safe(x + dx, y + dy));
  if (fits(0, 0)) return [0, 0];
  const xs = cells.map(c => c[0]), ys = cells.map(c => c[1]);
  const sx = (Math.min(...xs) + Math.max(...xs) + 1) / 2 > GRID / 2 ? -1 : 1;
  const sy = (Math.min(...ys) + Math.max(...ys) + 1) / 2 > GRID / 2 ? -1 : 1;
  const free = (dx, dy) => cells.every(([x, y]) => !clear.has(x + dx, y + dy));
  for (const strict of clear ? [true, false] : [false]) {
    for (let d = 1; d <= 12; d++) {
      for (let i = d; i >= 0; i--) {
        for (const [dx, dy] of [[sx * i, sy * (d - i)], [sx * i, -sy * (d - i)]]) {
          if (fits(dx, dy) && (!strict || free(dx, dy))) return [dx, dy];
        }
      }
    }
  }
  return [0, 0];
}

// Paints `draw` into `into`, moved as little as it takes to fit the circle.
// A prop that ends up touching the body gets a white sticker border, so it
// still reads as its own badge. Pass clear = null for props that sit on the
// body on purpose (a page held up, a keyboard, a tear).
function fitDraw(into, draw, clear = into) {
  const tmp = canvas();
  draw(tmp);
  const cells = [...tmp.cells].map(([k, color]) => { const [x, y] = k.split(',').map(Number); return [x, y, color]; });
  if (!cells.length) return;
  const [dx, dy] = fitShift(cells, clear);
  const moved = cells.map(([x, y, color]) => [x + dx, y + dy, color]);
  if (clear) {
    const own = new Set(moved.map(([x, y]) => `${x},${y}`));
    const ring = [];
    for (const [x, y] of moved) for (let i = -1; i <= 1; i++) for (let j = -1; j <= 1; j++) {
      if (!own.has(`${x + i},${y + j}`)) ring.push([x + i, y + j]);
    }
    if (ring.some(([x, y]) => clear.has(x, y))) for (const [x, y] of ring) if (safe(x, y)) into.put(x, y, '#FFFFFF');
  }
  for (const [x, y, color] of moved) into.put(x, y, color);
}

// The pose's body offset, cut back until the body fits: each bot gets the
// room its own shape leaves it.
function settle(p, draw) {
  // At rest every body fits (tools/fit.mjs checks it), so there's nothing to try.
  if (!p.dx && !p.dy && !p.lift) return p;
  for (const k of [1, 0.75, 0.5, 0.25, 0]) {
    const q = k === 1 ? p : { ...p, dx: p.dx * k, dy: p.dy * k, lift: (p.lift ?? 0) * k };
    const test = canvas();
    draw(test, q);
    if (allSafe(test)) return q;
  }
  return { ...p, dx: 0, dy: 0, lift: 0 };
}

// MARK: - Glyphs

// Sized for the room the drawer's circle leaves over a head: five rows at
// most, most of them four.
const PIX = {
  q: ['.##.', '#..#', '..#.', '....', '..#.'],
  excl: ['#', '#', '#', '.', '#'],
  z: ['####', '..#.', '.#..', '####'],
  zs: ['###', '.#.', '###'],
  note: ['..###', '..#.#', '..#..', '###..', '###..'],
  check: ['....#', '#..##', '####.', '.##..'],
  spark: ['.#.', '###', '.#.'],
  puff: ['.##.', '####', '.##.'],
  arrow: ['#...', '##..', '#w#.', '#ww#', '#.#.'],
  lock: ['.###.', '#...#', '#####', '##w##', '#####'],
  env: ['#######', '#w###w#', '##w#w##', '###w###', '#######'],
  prompt: ['#....', '.#...', '..#..', '.#...', '#.###'],
  wifi: ['.#####.', '#.....#', '..###..', '.#...#.', '...#...'],
  mag: ['.##..', '#ww#.', '#ww#.', '.##..', '...##'],
  tear: ['#', '#'],
  bubble: ['.#####.', '#ddddd#', '#ddddd#', '.#####.', '.#.....'],
  globe: [
    ['.###.', '#.#.#', '#####', '#.#.#', '.###.'],
    ['.###.', '##..#', '#####', '##..#', '.###.'],
    ['.###.', '#..##', '#####', '#..##', '.###.'],
  ],
};

// MARK: - Eyes

// Slit eyes, one pixel wide and two tall, at the given top cells. They
// glance a pixel toward the pointer and change shape with the mood.
function pxEyes(cv, at, c, mode, color, look = true) {
  const e = c.eye;
  const lx = look ? clamp(rnd(e.look[0] * 1.3), -1, 1) : 0;
  const ly = look ? clamp(rnd(e.look[1] * 1.2), -1, 1) : 0;
  const m = e.cross > 0.5 ? 'x' : e.happy > 0.5 ? 'happy' : e.sleep > 0.5 ? 'closed' : e.open < 0.45 ? 'blink' : mode;
  at.forEach(([x0, y0], i) => {
    const side = i === 0 ? -1 : 1;
    const wink = side > 0 && e.wink > 0.5;
    const x = x0 + lx, y = y0 + ly;
    const mm = wink ? 'happy' : m;
    if (mm === 'x') for (const [dx, dy] of [[-1, -1], [1, -1], [0, 0], [-1, 1], [1, 1]]) cv.put(x + dx, y + 1 + dy, color);
    else if (mm === 'happy') { cv.put(x - 1, y + 1, color); cv.put(x, y, color); cv.put(x + 1, y + 1, color); }
    // Content: closed and curved down, at peace.
    else if (mm === 'content') { cv.put(x - 1, y, color); cv.put(x, y + 1, color); cv.put(x + 1, y, color); }
    else if (mm === 'closed') cv.rect(side < 0 ? x - 1 : x, y + 1, 2, 1, color);
    // Resting eyes are half-closed: the lower cell only, still glancing.
    else if (mm === 'blink' || mm === 'focus' || mm === 'rest') cv.put(x, y + 1, color);
    else if (mm === 'wide') cv.rect(x, y - 1, 1, 3, color);
    else if (mm === 'sad') { cv.put(x, y + 1, color); cv.put(x + (side < 0 ? 1 : -1), y, color); }
    else cv.rect(x, y, 1, 2, color);
  });
}

// MARK: - Base poses

// What every character does in each phase unless it has its own way: body
// offsets in cells, limbs, eyes, and a prop with its own clock.
const PIXEL_REST = {
  dx: 0, dy: 0, sq: 0, armL: 'out', armR: 'out', legs: 'stand', eyes: 'open', dim: 0,
  prop: '', propT: 0, propU: 0, flip: 0,
};
const SETTLE_TIMES = ['finished', 'failed'];

function basePose(s, t, T) {
  const bob = frac(T / 1.5) > 0.8 ? 1 : 0;
  const walk = Math.floor(t * 8) % 2 ? 'walk1' : 'walk2';
  switch (s) {
    case 'thinking':
      return { armR: frac(t / 2.4) < 0.5 ? 'up' : 'out', look: [0.5 * Math.sign(Math.sin(t * 1.2)), -1], gazeW: 0.1, prop: 'think', propT: t };
    case 'streaming': {
      const beat = Math.floor(t * 5) % 2;
      return { armL: beat ? 'up' : 'out', armR: beat ? 'out' : 'up', dy: beat, look: [1, 0], gazeW: 0.2, prop: 'bubble', propT: t };
    }
    case 'reading':
      return { prop: 'page', propT: t, look: [Math.floor(frac(t / 1.6) * 3) - 1, 1], gazeW: 0 };
    case 'searching': {
      const u = frac(t / 6.4);
      const x = u < 0.25 ? stepsTo(u / 0.25, 0, -2) : u < 0.5 ? -2 : u < 0.75 ? stepsTo((u - 0.5) / 0.25, -2, 2) : 2;
      const moving = (u < 0.25 && u > 0.02) || (u > 0.5 && u < 0.75);
      const dir = u < 0.5 ? -1 : 1;
      return { dx: x, legs: moving ? walk : 'stand', armR: 'up', look: [dir, 0], gazeW: 0, prop: 'mag', propT: t, propU: dir };
    }
    case 'web':
      return { armL: 'high', armR: 'high', dy: Math.floor(t * 2) % 2, look: [0, -1], gazeW: 0, prop: 'globe', propT: t };
    case 'writing':
      return { armL: Math.floor(t * 8) % 2 ? 'tap' : 'down', armR: Math.floor(t * 8) % 2 ? 'down' : 'tap', dy: -2, look: [0, 1], gazeW: 0, prop: 'keys', propT: t };
    case 'running': {
      const f = Math.floor(t * 10) % 2;
      return { legs: f ? 'walk1' : 'walk2', dy: f ? 0 : -1, armL: f ? 'up' : 'down', armR: f ? 'down' : 'up', eyes: 'focus', look: [1, 0], gazeW: 0, prop: 'run', propT: t };
    }
    case 'controlling': {
      const u = frac(t / 1.2), i = Math.floor(t / 1.2) % 3;
      return { armR: i === 1 ? 'up' : 'out', armL: i === 2 ? 'up' : 'out', look: [[1, 1, -1][i], [1, -1, 0][i]], gazeW: 0, prop: 'mouse', propT: t, propU: u };
    }
    case 'spawning':
      return { armR: frac(t / 2.6) < 0.3 ? 'up' : 'out', look: [1, -1], gazeW: 0, prop: 'bud', propT: t, propU: frac(t / 2.6) };
    case 'orbit':
      return { dy: bob, look: [Math.cos(T * 1.3) > 0 ? 1 : -1, 0], gazeW: 0, prop: 'orbit', propT: T };
    case 'sending': {
      const u = frac(t / 2.2);
      return { armR: u < 0.3 ? 'down' : u < 0.55 ? 'high' : 'out', dy: u > 0.3 && u < 0.45 ? -1 : 0, look: [1, -1], gazeW: 0, prop: 'mail', propT: t, propU: u };
    }
    case 'asking': {
      const u = frac(t / 1.2);
      const air = u < 0.35;
      return { dy: air ? (u < 0.12 || u > 0.23 ? -1 : -2) : 0, legs: air ? 'jump' : 'stand', armL: air ? 'up' : 'out', armR: air ? 'up' : 'out', eyes: 'wide', look: [0, 0], gazeW: 0.4, prop: 'q', propT: t, propU: air ? 1 : 0 };
    }
    case 'approving':
      return { armL: 'high', armR: 'high', look: [0, 0], gazeW: 0.5, prop: 'sign', propT: t };
    case 'compacting': {
      const u = frac(t / 1.8);
      return { sq: u < 0.5 ? u / 0.5 : 1 - (u - 0.5) / 0.5, eyes: 'focus', gazeW: 0, prop: 'inward', propT: t };
    }
    case 'working': {
      const swing = Math.floor(t * 4) % 2;
      return { dx: -2, armR: swing ? 'high' : 'out', eyes: 'focus', look: [1, 1], gazeW: 0, prop: 'hammer', propT: t, propU: swing };
    }
    case 'finished': {
      const air = t < 0.5;
      return { dy: air ? (t < 0.15 || t > 0.35 ? -2 : -3) : 0, legs: air ? 'jump' : 'stand', armL: t < 1.2 ? 'high' : 'out', armR: t < 1.2 ? 'high' : 'out', happy: t < 1.4 ? 1 : 0, prop: 'sparks', propT: t, gazeW: 0.3 };
    }
    case 'listening': {
      const wave = frac(t / 5) > 0.75;
      return { armR: wave ? (Math.floor(t * 4) % 2 ? 'wave' : 'up') : 'out', dy: bob, look: [0, 0], gazeW: 1, prop: 'listen', propT: t };
    }
    case 'failed':
      return { dx: t < 0.6 ? (Math.floor(t * 16) % 2 ? 1 : -1) : 0, cross: 1, dim: smooth(t / 0.8), armL: 'down', armR: 'down', prop: 'smoke', propT: t, gazeW: 0 };
    case 'broken':
      return { dy: 2, legs: 'tuck', armL: 'down', armR: 'down', eyes: 'sad', dim: 1, look: [0, 1], gazeW: 0.2, prop: 'tear', propT: t };
    case 'offline':
      return { armL: 'down', armR: 'down', look: [Math.floor(t / 0.9) % 2 ? 1 : -1, 0], gazeW: 0.1, prop: 'nowifi', propT: t };
    case 'resting':
      // The agent's app is closed, minimized or in the tray: asleep, z's
      // rising.
      return { dy: 2, legs: 'tuck', armL: 'down', armR: 'down', sleep: 1, blink: 0, dim: 0.35, gazeW: 0, prop: 'zz', propT: T };
    case 'locked':
      return { armR: Math.floor(t * 2) % 3 === 0 ? 'high' : 'up', look: [1, -1], gazeW: 0, prop: 'lock', propT: t };
    case 'sleeping': {
      // Out of quota: settled down a cell or two, eyes closed and content,
      // one slow breath every 4 s. No props and no z's (those are resting's,
      // the app away). When the pointer comes close it half-opens its eyes
      // and looks; a poke opens them (stir, in the core).
      const up = frac(t / 4) >= 0.45 && frac(t / 4) < 0.8 ? 1 : 0;
      return { dy: 2 - up, legs: 'sit', armL: 'down', armR: 'down', eyes: 'content', blink: 0, look: [0, 0], gazeW: 0.6, stir: 1 };
    }
    case 'wave':
      return { armR: Math.floor(t * 4) % 2 ? 'wave' : 'up', happy: 1, gazeW: 0 };
    case 'hop': {
      const u = frac(t / 0.7);
      const air = t < 1.4 && u < 0.45;
      return { dy: air ? -2 : 0, legs: air ? 'jump' : 'stand', happy: 1, gazeW: 0 };
    }
    case 'stroll': {
      const x = t < 1.2 ? stepsTo(t / 1.2, 0, 3) : t < 1.6 ? 3 : t < 2.8 ? stepsTo((t - 1.6) / 1.2, 3, 0) : 0;
      const moving = t < 1.2 || (t > 1.6 && t < 2.8);
      return { dx: x, legs: moving ? walk : 'stand', look: [t < 1.4 ? 1 : -1, 0], gazeW: 0 };
    }
    case 'peek':
      return { look: t < 1.1 ? [-1, -0.2] : t < 2.3 ? [1, -0.2] : [0, 0], gazeW: 0, eyes: 'wide' };
    default:
      return { dy: bob };
  }
}

const PIXEL_CYCLE = {
  idle: 30, thinking: 6, streaming: 4, reading: 4.8, searching: 6.4, web: 6, writing: 4, running: 2.4,
  controlling: 4.8, spawning: 2.6, orbit: 9, sending: 2.2, asking: 2.4, approving: 4.8, compacting: 3.6,
  working: 3, finished: 1.7, listening: 9, failed: 1.5, broken: 10, offline: 6, sleeping: 8, locked: 6,
  resting: 8,
};

// MARK: - Shared props

// Draws the prop named in p.prop around a body. `a` is where the body ended
// up (mid column, top row, right/left edges, ground row); `pal` its colours
// and its mini-me. Returns false for props a character draws itself.
// Every prop stays inside the drawer's circle: floating ones go through
// fitDraw, which slides them in as little as it takes, clear of the body
// where there's room.
function pxProp(cv, back, p, a, pal, c) {
  const T = c.T, t = p.propT;
  const WHITE = '#FFFFFF', GRAY = '#8D8C94', SKY = '#7FB4FF';
  const top = a.top, mid = a.mid;
  const float = draw => fitDraw(cv, draw);
  switch (p.prop) {
    case 'bubble':
      float(g => {
        const x = mid + 1, y = top - 6;
        g.glyph(PIX.bubble, x, y, pal.ink ?? '#C9C2BE', { d: WHITE });
        for (let i = 0; i < 3; i++) g.put(x + 1 + i * 2, y + 1, Math.floor(t * 6) % 3 === i ? pal.main : '#C9C2BE');
      });
      return true;
    case 'page':
      fitDraw(cv, g => {
        // Held just under the eyes (a.front), so they peek over it.
        const x = a.pageX ?? mid - 4, y = a.front ?? top + 4;
        g.rect(x, y, 8, 5, pal.paper ?? '#FFF8F2');
        for (let j = 0; j < 2; j++) g.rect(x + 1, y + 1 + j * 2, [6, 4, 5][(j + Math.floor(t * 1.2)) % 3], 1, GRAY);
        if (a.hands) { g.rect(x - 1, y + 2, 1, 2, pal.body); g.rect(x + 8, y + 2, 1, 2, pal.body); }
      }, null);
      return true;
    case 'mag': {
      const dir = p.propU < 0 ? -1 : 1;
      float(g => {
        const gx = dir > 0 ? a.right - 3 : a.left - 2, gy = top - 5;
        g.glyph(PIX.mag, gx, gy, pal.dark, { w: WHITE }, dir < 0);
        if (Math.floor(t * 3) % 2) g.put(gx + (dir > 0 ? 1 : 3), gy + 1, '#CFE3FF');
      });
      return true;
    }
    case 'globe':
      float(g => g.glyph(PIX.globe[Math.floor(t * 4) % 3], mid - 2, top - 6, SKY));
      return true;
    case 'keys':
      // A small keyboard in front of the feet.
      fitDraw(cv, g => {
        const kx = mid - 5, ky = a.ground - 1;
        g.rect(kx, ky, 10, 2, '#5E5D66');
        for (let i = 0; i < 4; i++) g.put(kx + 1 + i * 2, ky, Math.floor(t * 8 + i * 3) % 5 === 0 ? WHITE : '#A5A4AD');
      }, null);
      return true;
    case 'run': {
      const u = frac(t * 2.5);
      fitDraw(back, g => g.glyph(PIX.puff, a.left - 3 - rnd(u * 2), a.ground - 3, '#D9D3CF'), null);
      float(g => {
        g.glyph(PIX.prompt, mid - 2, top - 6, pal.dark);
        if (Math.floor(t * 3) % 2) g.rect(mid + 1, top - 2, 2, 1, pal.main);
      });
      return true;
    }
    case 'mouse': {
      // The pointer visits three spots round the head and clicks at each.
      const spots = [[a.right - 2, top + 4], [mid + 2, top - 5], [a.left - 1, top - 3]];
      const i = Math.floor(t / 1.2) % 3, prev = spots[(i + 2) % 3], here = spots[i];
      const m = smooth(clamp(p.propU / 0.4));
      float(g => {
        const x = rnd(lerp(prev[0], here[0], m)), y = rnd(lerp(prev[1], here[1], m));
        g.glyph(PIX.arrow, x, y, pal.dark, { w: WHITE });
        if (p.propU > 0.55 && p.propU < 0.8) for (const [dx, dy] of [[-1, -1], [1, -2], [3, 0]]) g.put(x + dx, y + dy, pal.main);
      });
      return true;
    }
    case 'bud': {
      // A mini-me buds on the shoulder and hops up and away.
      const u = p.propU;
      const x0 = a.right - 5, y0 = top - 4;
      if (u < 0.3) { if (u > 0.1) float(g => g.rect(x0 + 2, y0 + 2, 2, 2, pal.main)); return true; }
      const v = (u - 0.3) / 0.7;
      const hop = Math.floor(v * 3);
      if (u < 0.92) float(g => pal.mini(g, x0 - hop * 2, y0 - hop - (frac(v * 3) < 0.5 ? 1 : 0)));
      return true;
    }
    case 'orbit':
      for (let i = 0; i < 3; i++) {
        const ang = steps(T, 6) * 1.3 + i * TAU / 3;
        const x = rnd(mid - 4 + Math.cos(ang) * 5), y = rnd(a.midY - 2 + Math.sin(ang) * 2);
        fitDraw(Math.sin(ang) < 0 ? back : cv, g => pal.mini(g, x, y), null);
      }
      return true;
    case 'mail': {
      // The letter lifts from the hand and flies up over the head.
      const u = p.propU;
      const from = [a.right - 3, top - 4], to = [mid - 3, top - 7];
      if (u < 0.3) { float(g => g.glyph(PIX.env, from[0], from[1], WHITE, { w: GRAY })); return true; }
      const v = clamp((u - 0.3) / 0.5);
      if (v < 1) float(g => g.glyph(PIX.env, rnd(lerp(from[0], to[0], v)), rnd(lerp(from[1], to[1], v)), WHITE, { w: GRAY }));
      else if (u < 0.88) float(g => g.glyph(PIX.spark, mid - 1, top - 6, pal.main));
      return true;
    }
    case 'q':
      float(g => g.glyph(PIX.q, mid + 3, top - 6 - (p.propU > 0.5 ? 1 : 0), pal.accent ?? pal.main));
      return true;
    case 'sign': {
      // A green tick held up, pulsing.
      const pulse = Math.floor(t * 2.5) % 2;
      float(g => g.glyph(PIX.check, mid - 2, top - 5, pulse ? '#3A9F5B' : '#5BBF7A'));
      return true;
    }
    case 'inward':
      for (let i = 0; i < 6; i++) {
        const u = frac(steps(t, 8) / 0.9 + i / 6);
        const ang = i * 60 + 15;
        const r = lerp(9, 4, u);
        const x = rnd(mid + 0.5 + Math.sin(rad(ang)) * r), y = rnd(a.midY - Math.cos(rad(ang)) * r * 0.8);
        if (safe(x, y)) cv.put(x, y, pal.light);
      }
      return true;
    case 'hammer': {
      // Driving a post in beside it, never hitting its own head (the user,
      // 2026-10-19). The post stands right of the body where the circle is
      // widest, and the bot steps left as far as it fits: the hammer goes up
      // beside the head, then down on the post's cap.
      const up = p.propU;
      const x = 18, y = 11;
      fitDraw(cv, g => {
        g.rect(x, y, 2, 3, '#C8955A');
        g.rect(x + 1, y, 1, 3, '#A4743F');
        g.rect(x, y - 1, 2, 1, '#5E616C');
        if (up) {
          g.rect(x - 3, y - 6, 3, 2, GRAY);
          g.put(x - 2, y - 4, '#B07A45');
        } else {
          g.rect(x, y - 3, 2, 2, GRAY);
          g.rect(x - 1, y - 2, 1, 2, '#B07A45');
          if (Math.floor(t * 8) % 2) g.put(x - 1, y - 4, '#FFD36B');
        }
      }, null);
      return true;
    }
    case 'sparks':
      if (t < 1.4) for (const [dx, dy, k] of [[a.left - 2, top - 3, 0], [a.right - 1, top - 4, 1], [a.left - 3, top + 2, 2], [a.right + 1, top + 1, 3]]) {
        if (Math.floor(t * 6 + k) % 2) float(g => g.glyph(PIX.spark, dx, dy, k % 2 ? '#FFD36B' : pal.main));
      }
      return true;
    case 'smoke': {
      const u = frac(t / 0.9);
      float(g => g.glyph(PIX.puff, mid - 2 + (Math.floor(t * 3) % 2), top - 4 - rnd(u * 2), '#BDB7B4'));
      return true;
    }
    case 'tear': {
      const u = frac(t / 1.6);
      if (u < 0.7 && a.eyes) fitDraw(cv, g => g.glyph(PIX.tear, a.eyes[0][0], a.eyes[0][1] + 2 + rnd(u * 3), SKY), null);
      return true;
    }
    case 'nowifi': {
      const on = Math.floor(t * 2) % 2;
      float(g => {
        const x = mid - 3, y = top - 6;
        g.glyph(PIX.wifi, x, y, on ? pal.dark : GRAY);
        if (on) for (const [dx, dy] of [[0, 0], [1, 1], [3, 2], [5, 3], [6, 4]]) g.put(x + dx, y + dy, '#D9534F');
      });
      return true;
    }
    case 'zz':
      // Two z's rise from beside the head.
      for (let i = 0; i < 2; i++) {
        const u = frac(t / 2.4 + i / 2);
        if (u < 0.85) float(g => g.glyph(i ? PIX.zs : PIX.z, a.right - 4 + i * 2, top - 2 - rnd(u * 4) - i * 2, pal.light));
      }
      return true;
    case 'lock':
      float(g => g.glyph(PIX.lock, mid + 1 + (Math.floor(t * 3) % 2), top - 6, '#C9A23A', { w: pal.dark }));
      return true;
    default:
      return false;
  }
}

// MARK: - Claude · Clawd
//
// Claude Code's own mascot from its terminal banner (▐▛███▜▌ / ▝▜█████▛▘ /
// ▘▘ ▝▝): a terracotta block with two slit eyes, arm nubs and four legs —
// the template the whole pixel family follows. It thinks under the CLI's
// sparkle spinner (· ✢ ✳ ✶ ✻), reads a page, types on a tiny keyboard,
// hammers away, walks about with a magnifier and sleeps sitting down.

(() => {
  const ORANGE = '#D77757';
  const DULL = '#A98272';
  const DARK = '#21120C';
  const LIGHT = '#F2A585';
  // One row up from round 3, so the idle bob keeps the feet inside the circle.
  const W0 = 12, H0 = 8, X0 = 5, Y0 = 8;
  const SPINNER = [
    ['.....', '.....', '..#..', '.....', '.....'],
    ['.....', '..#..', '.###.', '..#..', '.....'],
    ['..#..', '..#..', '#####', '..#..', '..#..'],
    ['#.#.#', '.###.', '#####', '.###.', '#.#.#'],
    ['#...#', '.#.#.', '..#..', '.#.#.', '#...#'],
    ['..#..', '#.#.#', '.###.', '#.#.#', '..#..'],
  ];
  const MINI = ['.######.', '.#.##.#.', '########', '.######.', '..#..#..'];
  const pal = {
    main: ORANGE, body: ORANGE, dark: DARK, light: LIGHT, accent: ORANGE,
    mini: (cv, x, y) => { cv.glyph(MINI, x, y, ORANGE); cv.put(x + 2, y + 1, DARK); cv.put(x + 5, y + 1, DARK); },
  };

  function clawd(cv, p, c, color) {
    const W = rnd(lerp(W0, 8, p.sq)), H = rnd(lerp(H0, 6, p.sq));
    const x0 = X0 + rnd(p.dx) + rnd((W0 - W) / 2), y0 = Y0 + rnd(p.dy) + (H0 - H);
    const legs = { stand: [2, 2, 2, 2], walk1: [1, 2, 1, 2], walk2: [2, 1, 2, 1], tuck: [0, 0, 0, 0], jump: [1, 1, 1, 1], sit: [1, 1, 1, 1] }[p.legs] ?? [2, 2, 2, 2];
    const legCols = p.legs === 'jump' ? [0, 2, W - 3, W - 1] : [1, 3, W - 4, W - 2];
    legCols.forEach((lc, i) => { if (legs[i]) cv.rect(x0 + lc, y0 + H, 1, legs[i], color); });
    cv.rect(x0, y0, W, H, color);
    const arm = (side, mode) => {
      const ax = side < 0 ? x0 - 2 : x0 + W;
      const ay = y0 + 4 - (H0 - H > 0 ? 1 : 0);
      const outer = side < 0 ? ax : ax + 1, inner = side < 0 ? ax + 1 : ax;
      if (mode === 'out') cv.rect(ax, ay, 2, 2, color);
      else if (mode === 'up') { cv.rect(ax, ay, 2, 1, color); cv.rect(outer, ay - 2, 1, 2, color); }
      else if (mode === 'high') { cv.rect(inner, ay - 3, 1, 4, color); cv.put(outer, ay - 4, color); }
      else if (mode === 'down') { cv.rect(ax, ay + 1, 2, 1, color); cv.put(outer, ay + 2, color); }
      else if (mode === 'wave') { cv.rect(ax, ay - 1, 2, 2, color); cv.put(side < 0 ? ax - 1 : ax + 2, ay - 2, color); }
      else if (mode === 'tap') cv.rect(ax, ay + 2, 2, 1, color);
    };
    arm(-1, p.armL);
    arm(1, p.armR);
    const eyes = [[x0 + 2, y0 + 2], [x0 + W - 3, y0 + 2]];
    pxEyes(cv, eyes, c, p.eyes, DARK);
    return { x0, y0, W, H, mid: x0 + Math.floor(W / 2), top: y0, left: x0, right: x0 + W, ground: y0 + H + 2, midY: y0 + 4, front: y0 + 4, eyes, hands: true };
  }

  CHARACTERS.claude = {
    name: 'Claude',
    swatch: 0xD77757,
    fidgets: ['wave', 'hop', 'stroll'],
    cycle: PIXEL_CYCLE,
    rest: PIXEL_REST,
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        // Offline: its spinner's sparkle stalls, grey, a dot blinking on.
        case 'offline': return { ...b, prop: 'stall' };
        // Locked: a padlock hangs on its front; it tugs with both arms.
        case 'locked': return { ...b, armL: b.armR, prop: 'padlock' };
        default: return b;
      }
    },
    draw(p, c) {
      const color = p.dim > 0.5 ? DULL : ORANGE;
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => clawd(g, q, c, color));
      const a = clawd(cv, q, c, color);
      const body = { ...pal, body: color };
      if (q.prop === 'think') fitDraw(cv, g => g.glyph(SPINNER[Math.floor(q.propT * 7) % SPINNER.length], a.mid - 2, a.top - 7, ORANGE));
      else if (q.prop === 'stall') fitDraw(cv, g => {
        g.glyph(SPINNER[3], a.mid - 2, a.top - 7, '#A79A94');
        if (Math.floor(q.propT * 2) % 2) g.put(a.mid + 4, a.top - 3, '#A79A94');
      });
      else if (q.prop === 'padlock') {
        const up = Math.floor(q.propT * 2) % 3 === 0 ? 1 : 0;
        fitDraw(cv, g => g.glyph(PIX.lock, a.mid - 2, a.top + 3 - up, '#C9A23A', { w: DARK }), null);
      }
      else pxProp(cv, back, q, a, body, c);
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// MARK: - Codex · the notebook
//
// A spiral notebook, drawn from Codex's own concept Folio (a page with a blue
// bookmark, a gold dog-ear and bracket hands) rather than traced from it:
// Folio's bookmark becomes the elastic band down the cover, its dog-ear the
// gold ribbon out of the bottom, its hands two little ink arms. A title label
// sits under the rings and a pencil is tucked into the band. Its rings
// rattle while it thinks, it writes along its cover, a red scribble crosses
// it out when something fails, and it sits down to rest. (The user's pick,
// 2026-10-17, over round 4's puff cloud, which is kept in retired/.)

(() => {
  const COVER = '#FFF2D8', COVER_SHADE = '#EADBBE', INK = '#263349';
  const BAND = '#4D96EB', BAND_DARK = '#3166A4', GOLD = '#F2BB55', GOLD_DARK = '#C9922E';
  const RING = '#8D94A6', LABEL = '#FFFFFF', LINE = '#4D96EB', WRITING = '#8FB4E8', RED = '#D9534F';
  const ERASER = '#F09A9A', FERRULE = '#C3C8D4';
  const DIM_COVER = '#D9D2C4', DIM_SHADE = '#C8C0B0', DIM_BAND = '#8C9DB5', DIM_GOLD = '#BDB4A1';
  // 12 × 9 with its rings above and ribbon below; low enough that it can hop
  // a cell and still sit two cells down to rest inside the drawer's circle.
  const W = 12, H = 9, X0 = 5, Y0 = 6;
  const MINI = ['.#.#.', 'cccbb', 'cccbb', 'cccbb', '..g..'];
  const pal = {
    main: BAND, body: COVER, dark: INK, light: '#9DBFEA', accent: BAND, ink: INK, paper: '#FFFFFF',
    mini: (cv, x, y) => cv.glyph(MINI, x, y, RING, { c: COVER, b: BAND, g: GOLD }),
  };
  // Little arms, 1 × 2, against the cover: [cells in toward it, row], raised
  // arms stepping in, the room the circle leaves.
  const ARMS = { out: [1, 4], up: [1, 2], high: [1, 0], wave: [1, 1], down: [1, 6], tap: [2, 7] };
  // What it writes along its cover, word by word: [column, length].
  const WORDS = [[1, 3], [5, 2], [8, 1]];

  function notebook(cv, p, c) {
    const dim = p.dim > 0.5;
    const x0 = X0 + rnd(p.dx), y0 = Y0 + rnd(p.dy);
    const cells = [];
    const put = (x, y, color) => { cv.put(x, y, color); cells.push([x, y]); };
    for (let j = 0; j < H; j++) for (let i = 0; i < W; i++) {
      const shade = j === H - 1 || i === W - 1;
      put(x0 + i, y0 + j, dim ? (shade ? DIM_SHADE : DIM_COVER) : shade ? COVER_SHADE : COVER);
    }
    // The elastic band down the right of the cover.
    for (let j = 0; j < H; j++) { put(x0 + 9, y0 + j, dim ? DIM_BAND : BAND); put(x0 + 10, y0 + j, dim ? DIM_BAND : BAND_DARK); }
    // The ribbon out of the bottom.
    put(x0 + 5, y0 + H, dim ? DIM_GOLD : GOLD);
    put(x0 + 5, y0 + H + 1, dim ? DIM_GOLD : GOLD_DARK);
    cv.outline(cells, INK);
    // Feet, under the outline.
    const legs = { stand: [1, 1], walk1: [0, 1], walk2: [1, 0], jump: [0, 0], tuck: [0, 0], sit: [0, 0] }[p.legs] ?? [1, 1];
    if (legs[0]) cv.rect(x0 + 2, y0 + H + 1, 2, 1, INK);
    if (legs[1]) cv.rect(x0 + 7, y0 + H + 1, 2, 1, INK);
    // The spiral: rings over the top edge, punched holes under them; they
    // rattle while it thinks.
    const rattle = p.rattle ? Math.floor(c.T * 8) % 2 : -1;
    const gone = p.lost ? Math.floor(c.T * 3) % 6 : -1;
    for (let i = 0; i < 6; i++) {
      const x = x0 + 1 + i * 2, up = i % 2 === rattle ? 1 : 0;
      if (p.lost) { if (i !== gone && i !== (gone + 3) % 6) cv.put(x, y0 - 1, '#C3C8D4'); }
      else cv.put(x, y0 - 1 - up, RING);
      if (up) cv.put(x, y0 - 1, INK);
      if (i < 4) cv.put(x, y0, INK);
    }
    // The title label under the rings, and the pencil tucked into the band.
    cv.rect(x0 + 2, y0 + 1, 5, 2, dim ? '#E6E1D6' : LABEL);
    cv.rect(x0 + 3, y0 + 1, 3, 1, dim ? '#8A8F9C' : LINE);
    cv.put(x0 + 10, y0 - 2, dim ? '#C9B8B8' : ERASER);
    cv.put(x0 + 10, y0 - 1, dim ? '#B8B8B8' : FERRULE);
    for (let j = 0; j <= 3; j++) cv.put(x0 + 10, y0 + j, dim ? DIM_GOLD : GOLD);
    for (const [side, mode] of [[-1, p.armL], [1, p.armR]]) {
      const [inward, row] = ARMS[mode] ?? ARMS.out;
      cv.rect(side < 0 ? x0 - 3 + inward : x0 + W + 2 - inward, y0 + row, 1, 2, INK);
    }
    // Locked: a diary clasp over the band, its keyhole dark.
    if (p.clasp) {
      cv.rect(x0 + 8, y0 + 3, 4, 3, dim ? DIM_GOLD : GOLD);
      cv.rect(x0 + 8, y0 + 5, 4, 1, dim ? DIM_GOLD : GOLD_DARK);
      cv.rect(x0 + 9, y0 + 3, 1, 2, INK);
    }
    const eyes = [[x0 + 2, y0 + 4], [x0 + 6, y0 + 4]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    return { x0, y0, eyes, hands: true, mid: x0 + 5, top: y0 - 2, left: x0, right: x0 + W - 1, ground: y0 + H + 2, midY: y0 + 4, front: y0 + 6, pageX: x0 + 1 };
  }

  CHARACTERS.codex = {
    name: 'Codex',
    swatch: 0x9EA1AB,
    fidgets: ['wave', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, rattle: 0, lost: 0, clasp: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        // Thinking: its rings rattle, dots counting beside it.
        case 'thinking': return { ...b, rattle: 1, prop: 'dots' };
        // Writing: words write themselves along the cover as its arms tap.
        case 'writing': return { ...b, armL: Math.floor(t * 8) % 2 ? 'tap' : 'down', armR: Math.floor(t * 8) % 2 ? 'down' : 'tap', dy: 0, prop: 'words', look: [0, 1] };
        case 'failed': return { ...b, prop: 'cross' };
        case 'offline': return { ...b, lost: 1, prop: '' };
        case 'locked': return { ...b, clasp: 1, prop: '', look: [1, 0] };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => notebook(g, q, c));
      const a = notebook(cv, q, c);
      const t = q.propT;
      switch (q.prop) {
        case 'dots': {
          const n = 1 + Math.floor(t * 2) % 3;
          fitDraw(cv, g => { for (let i = 0; i < n; i++) g.put(a.right + 2 + i, a.top + 1 - i, BAND); });
          break;
        }
        case 'words': {
          // Word by word along the cover, under the face, then again.
          const n = rnd(clamp(frac(t / 2.4) / 0.8) * 6);
          let k = 0;
          for (const [col, len] of WORDS) for (let i = 0; i < len; i++) if (k++ < n) cv.put(a.x0 + col + i, a.y0 + 7, WRITING);
          break;
        }
        case 'cross':
          // A red scribble crosses the cover out.
          if (t > 0.2) for (const [dx, dy] of [[2, 6], [6, 6], [3, 7], [4, 7], [5, 7], [2, 8], [6, 8]]) cv.put(a.x0 + dx, a.y0 + dy, RED);
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// MARK: - Antigravity · the balloon
//
// A hot-air balloon, drawn from Codex's own concept Aeri (a floating blue
// "A" with a cream face band, a coral mouth and four satellites) rather than
// traced from it: Aeri floats, so this one flies. Her four satellites (blue,
// coral, yellow, green) become the gores of its envelope, curving in at the
// crown and the skirt; her face band runs round its middle, with her coral
// mouth on it when it's happy; a wicker basket hangs below with a sandbag each side. Its
// gores turn while it thinks, its burner flares while it works, its envelope
// sags onto the basket when it fails, and it lands to rest. (The user's
// pick, 2026-10-17, over round 4's rainbow "A", which is kept in retired/.)

(() => {
  const GORES = [['#4D96EB', '#B7D5F5', '#3A7BC8'], ['#E77C65', '#F6B4A6', '#C9604B'], ['#F2BB55', '#FBE2AE', '#D49A33'], ['#56AB78', '#A9DDBB', '#3F8A5D']];
  const DIM_GORES = [['#9EA6B4', '#C7CCD6', '#868E9C'], ['#B2A9A6', '#D3CDCB', '#988F8C'], ['#BDB4A1', '#DCD6CA', '#A39A88'], ['#A3ABA5', '#C9CFCB', '#8A928C']];
  const FACE = '#FFF2D8', DIM_FACE = '#D9D2C4', INK = '#263349', MOUTH = '#E77C65';
  const WICKER = '#D9A04A', WICKER_DARK = '#A8742E', RIM = '#7A5226', BAG = '#E6D3AE', BAG_DARK = '#BFA77A', BURNER = '#8D94A6';
  const DIM_WICKER = '#B8AE9C', DIM_RIM = '#8E8678';
  const FLAME = ['#F2BB55', '#E77C65'];
  const X0 = 5, Y0 = 3;
  // The envelope's rows, [first, last] cell from its left edge: a round
  // crown, widest a third of the way down, tapering to the skirt.
  const ENVELOPE = [[4, 7], [2, 9], [1, 10], [0, 11], [0, 11], [0, 11], [0, 11], [1, 10], [2, 9], [3, 8], [4, 7]];
  // Let down (failed, broken): squat and slumped onto the basket.
  const SAGGED = [[3, 8], [1, 10], [0, 11], [0, 11], [0, 11], [1, 10], [3, 8]];
  const FACE_ROWS = [4, 5, 6];
  const MINI = ['.##.', '####', '####', '.##.', '.#..'];
  const pal = {
    main: GORES[0][0], body: GORES[0][0], dark: INK, light: GORES[0][1], accent: MOUTH,
    mini: (cv, x, y) => { cv.glyph(MINI, x, y, GORES[0][0]); cv.put(x + 2, y + 1, GORES[1][0]); cv.put(x + 2, y + 2, GORES[1][0]); cv.put(x + 1, y + 4, WICKER); },
  };

  function balloon(cv, p, c) {
    const dim = p.dim > 0.5;
    const x0 = X0 + rnd(p.dx), y0 = Y0 + rnd(p.dy);
    const sag = p.sag > 0.5;
    const rows = sag ? SAGGED : ENVELOPE;
    // A sagging envelope slumps down onto the basket.
    const top = y0 + (sag ? ENVELOPE.length - SAGGED.length + 2 : 0);
    const spin = rnd(p.spin ?? 0);
    const gores = dim ? DIM_GORES : GORES;
    const faceRows = sag ? [2, 3, 4] : FACE_ROWS;
    rows.forEach(([a, b], j) => {
      const w = b - a + 1;
      for (let i = a; i <= b; i++) {
        // The gores curve in at the crown and the skirt: each row splits its
        // own width four ways.
        const g = gores[((Math.floor((i - a) * 4 / w) + spin) % 4 + 4) % 4];
        const shine = !sag && ((j === 1 && i === 3) || (j === 2 && i === 2) || (j === 3 && i === 1));
        const color = faceRows.includes(j) ? (dim ? DIM_FACE : FACE) : shine ? g[1] : j === rows.length - 1 ? g[2] : g[0];
        cv.put(x0 + i, top + j, color);
      }
    });
    // Ropes and the burner, the basket and its sandbags.
    const bottom = y0 + 13;
    if (!sag) {
      for (const i of [4, 7]) { cv.put(x0 + i, y0 + 11, p.lock ? BURNER : INK); cv.put(x0 + i, y0 + 12, INK); }
      cv.rect(x0 + 5, y0 + 12, 2, 1, BURNER);
      if (p.smoke) {
        // A puff from the dead burner, drifting out past the right rope.
        const u = frac(c.T / 1.6);
        if (u < 0.8) cv.rect(x0 + 8 + rnd(u * 2), y0 + 11 - rnd(u * 2), 2, 1, '#BDB7B4');
      }
      if (p.flame) {
        const f = Math.floor(c.T * 8) % 2;
        cv.put(x0 + 5, y0 + 11, FLAME[f]); cv.put(x0 + 6, y0 + 11, FLAME[1 - f]);
      }
    }
    cv.rect(x0 + 3, bottom, 6, 1, dim ? DIM_RIM : RIM);
    for (let i = 0; i < 6; i++) cv.put(x0 + 3 + i, bottom + 1, dim ? DIM_WICKER : i % 2 ? WICKER_DARK : WICKER);
    cv.rect(x0 + 3, bottom + 2, 6, 1, dim ? DIM_RIM : WICKER_DARK);
    for (const x of [x0 + 2, x0 + 9]) { cv.put(x, bottom, dim ? DIM_WICKER : BAG); cv.put(x, bottom + 1, dim ? DIM_WICKER : BAG_DARK); }
    // Locked: a steel padlock hangs on the basket's front, its shackle round the
    // burner, so it can't take off.
    if (p.lock && !sag) {
      cv.rect(x0 + 4, bottom, 4, 2, '#B9BECB');
      cv.rect(x0 + 4, bottom + 1, 4, 1, BURNER);
      cv.put(x0 + 5, bottom, INK);
      cv.put(x0 + 5, bottom + 1, INK);
    }
    const eyeRow = top + faceRows[0];
    const eyes = [[x0 + 3, eyeRow], [x0 + 8, eyeRow]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    // The coral mouth shows only when it's happy (the user, 2026-10-19).
    if (c.eye.happy > 0.5) cv.rect(x0 + 5, eyeRow + 2, 2, 1, dim ? '#B2A9A6' : MOUTH);
    return { x0, y0: top, eyes, mid: x0 + 6, top, left: x0, right: x0 + 11, ground: bottom + 2, midY: top + 5, front: top + 7, pageX: x0 + 2 };
  }

  CHARACTERS.antigravity = {
    name: 'Antigravity',
    swatch: 0x3D7BF5,
    fidgets: ['spin', 'puff', 'hop'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, spin: 0, flame: 0, sag: 0, lock: 0, smoke: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      // Rising and sinking on the air, a cell at a time.
      const ride = frac(T / 3) < 0.5 ? 0 : -1;
      switch (s) {
        case 'idle': return { ...b, dy: ride };
        // Thinking: the envelope turns slowly, gore by gore.
        case 'thinking': return { ...b, dy: ride, spin: Math.floor(t / 0.6) % 4, look: [0, -1], prop: '' };
        // Busy: the burner flares.
        case 'writing': case 'working': case 'running': return { ...b, dy: 0, flame: 2, prop: s === 'working' ? b.prop : '' };
        case 'failed': return { ...b, sag: t > 0.3 ? 1 : 0, dy: 0 };
        case 'broken': return { ...b, sag: 1, dy: 0 };
        // Offline: the burner has gone out; it hangs low, smoke drifting off.
        case 'offline': return { ...b, dy: 1, smoke: 1, prop: '' };
        // Locked: a padlock on the burner, so it can't take off; landed.
        case 'locked': return { ...b, dy: 1, lock: 1, prop: '', look: [0, 1] };
        // Asleep (its app away) it has landed, basket on the ground.
        case 'resting': return { ...b, dy: 1 };
        // Out of quota: landed too, rising a cell on each slow breath.
        case 'sleeping': return { ...b, dy: b.dy - 1 };
        // Fidgets: one full turn; a puff of the burner and a lift.
        case 'spin': return { ...b, spin: t > 0.4 && t < 2.0 ? Math.floor((t - 0.4) / 0.4) + 1 : 0, happy: t > 0.4 && t < 2.0 ? 1 : 0, gazeW: 0 };
        case 'puff': return { ...b, flame: t > 0.3 && t < 1.2 ? 2 : 0, dy: t > 0.6 && t < 1.8 ? -1 : 0, look: [0, -1], gazeW: 0 };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => balloon(g, q, c));
      const a = balloon(cv, q, c);
      pxProp(cv, back, q, a, pal, c);
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// MARK: - Grok · the slashed ring
//
// Grok's own ⊘ as a character, in the Grok Bot spirit: a black ring round a
// pale face, the slash passing behind it and out at both ends, dark pill
// eyes and no limbs. Its eyes do the talking: three dots walk across them
// while it thinks, they go wide to search, turn into a "!" to ask and close
// in sleep. It searches with a ring that steps outward, wears a planet's
// ring for the web, rides a flame to run and swirls inward to compact.
// (Round 4, 2026-10-15: the user's pick over a solid orb; round 3's ring
// bot with antenna and legs retired.)

(() => {
  const INK = '#0B0B0C', DIM_INK = '#5A5A60', FACE = '#F4F4F1', DIM_FACE = '#CFCFCB', DOT = '#B4B4B0';
  // Centred in the box, so the slash reaches two cells out at both ends and
  // stays inside the drawer's circle.
  const CX = 11, CY = 11;
  const MINI = ['..#.#', '.###.', '#w#w#', '#www#', '.###.', '#....'];
  const pal = {
    main: INK, body: INK, dark: INK, light: '#9A9AA0', accent: INK,
    mini: (cv, x, y) => cv.glyph(MINI, x, y, INK, { w: FACE }),
  };
  const lookOf = c => [clamp(rnd(c.eye.look[0] * 1.3), -1, 1), clamp(rnd(c.eye.look[1] * 1.2), -1, 1)];

  // Pill eyes, 2 wide and `h` tall, centred on cx + 0.5; they morph with the
  // mood and the phase.
  function pillEyes(cv, cx, cy, p, c, ink, dim, h) {
    const [lx, ly] = lookOf(c);
    const top = cy - 2 + ly;
    const xs = [cx - 3 + lx, cx + 2 + lx];
    const e = c.eye, t = p.propT;
    if (p.face === 'dots') {
      // Thinking: three dots across the face, the lit one walking.
      const k = Math.floor(t * 4) % 3;
      [cx - 4, cx, cx + 4].forEach((x, i) => cv.rect(x + lx, cy - 1 + ly, 2, 2, i === k ? ink : dim));
      return;
    }
    if (p.face === 'bang') {
      cv.rect(cx, cy - 4, 2, 4, ink);
      cv.rect(cx, cy + 1, 2, 2, ink);
      return;
    }
    xs.forEach((x, i) => {
      const right = i === 1;
      const mood = e.cross > 0.5 ? 'cross' : (e.happy > 0.5 || (right && e.wink > 0.5)) ? 'happy' : e.sleep > 0.5 ? 'closed'
        : e.open < 0.45 ? 'blink' : p.eyes;
      if (mood === 'cross') for (const [dx, dy] of [[-1, 0], [2, 0], [0, 1], [1, 1], [0, 2], [1, 2], [-1, 3], [2, 3]]) cv.put(x + dx, top + dy, ink);
      else if (mood === 'happy') for (const [dx, dy] of [[-1, 2], [0, 1], [1, 1], [2, 2]]) cv.put(x + dx, top + dy, ink);
      else if (mood === 'content') for (const [dx, dy] of [[-1, 2], [0, 3], [1, 3], [2, 2]]) cv.put(x + dx, top + dy, ink);
      else if (mood === 'closed') cv.rect(x - (right ? 0 : 1), top + 3, 3, 1, ink);
      // Resting holds the eyes half-closed, the shape of a blink.
      else if (mood === 'blink' || mood === 'rest') cv.rect(x, top + 2, 2, 1, ink);
      else if (mood === 'focus') cv.rect(x, top + 1, 2, 2, ink);
      else if (mood === 'wide') cv.rect(x, top - 1, 2, h + 1, ink);
      else if (mood === 'sad') { cv.rect(x, top + 2, 2, 2, ink); cv.put(right ? x : x + 1, top + 1, ink); }
      else cv.rect(x, top, 2, h, ink);
    });
  }

  function ring(cv, p, c) {
    const cx = CX + rnd(p.dx), cy = CY + rnd(p.dy) - rnd(p.lift);
    const dim = p.dim > 0.5;
    const ink = dim ? DIM_INK : INK, face = dim ? DIM_FACE : FACE;
    const inner = p.sq > 0.5 ? 3.4 : 4.5;
    const tick = Math.floor(c.T * 6);
    // The slash, behind the face: out past the ring at both ends.
    for (const [dx, dy] of [[5, -5], [6, -6], [-5, 5], [-6, 6]]) cv.put(cx + dx, cy + dy, ink);
    cv.shape((x, y) => Math.hypot(x - cx - 0.5, y - cy - 0.5) <= 6.5, cx - 7, cy - 7, 15, 15, (x, y) => {
      const d = Math.hypot(x + 0.5 - cx - 0.5, y + 0.5 - cy - 0.5);
      if (d < inner) return face;
      if (p.dashed && (Math.floor(Math.atan2(y - cy, x - cx) / (TAU / 16)) + tick) % 2) return face;
      return ink;
    });
    if (p.glow && Math.floor(c.T * 4) % 2) cv.put(cx + 5, cy - 7, '#F5BE2E');
    pillEyes(cv, cx, cy, p, c, ink, DOT, 3);
    return { mid: cx, top: cy - 8, left: cx - 7, right: cx + 8, ground: cy + 8, midY: cy, front: cy + 1, eyes: [[cx - 3, cy - 2], [cx + 2, cy - 2]], cx, cy, hands: false };
  }

  CHARACTERS.grok = {
    name: 'Grok',
    swatch: 0x0B0B0C,
    fidgets: ['wink', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, face: '', glow: 0, lift: 0, dashed: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        case 'thinking': return { ...b, face: 'dots', glow: 1, prop: '' };
        case 'searching': return { ...b, dx: 0, legs: 'stand', eyes: 'wide', prop: 'radar' };
        case 'web': return { ...b, dy: 0, prop: 'planet' };
        case 'running': return { ...b, lift: 2 + (Math.floor(t * 10) % 2), dy: 0, prop: 'flame' };
        case 'compacting': return { ...b, prop: 'swirl' };
        case 'asking': return { ...b, face: 'bang', prop: '' };
        case 'approving': return { ...b, glow: 1 };
        case 'offline': return { ...b, dashed: 1, prop: '' };
        case 'locked': return { ...b, prop: 'shackle', look: [0, -1] };
        case 'finished': return { ...b, glow: 1 };
        case 'wink': return { ...b, wink: t > 0.2 && t < 1.3 ? 1 : 0, gazeW: 0.3 };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => ring(g, q, c));
      const a = ring(cv, q, c);
      const t = q.propT;
      // Rings, lines and sparks show only where the circle allows.
      const dot = (into, x, y, color) => { if (safe(x, y)) into.put(x, y, color); };
      switch (q.prop) {
        case 'radar': {
          // A ring steps outward from the body, then again.
          const k = Math.floor(t * 4) % 3;
          const r = [7.2, 8.2, 9.2][k];
          for (let x = 0; x < GRID; x++) for (let y = 0; y < GRID; y++) {
            const d = Math.hypot(x + 0.5 - a.cx - 0.5, y + 0.5 - a.cy - 0.5);
            if (Math.abs(d - r) < 0.5 && !cv.has(x, y)) dot(k === 2 ? back : cv, x, y, k === 0 ? '#8D8C94' : '#B9B9C0');
          }
          break;
        }
        case 'planet': {
          const y = a.cy + 1;
          for (let x = 0; x < GRID; x++) {
            const inFace = Math.abs(x - a.cx) <= 6;
            dot(inFace ? cv : back, x, inFace ? y + 1 : y, INK);
          }
          const m = Math.floor(t * 6) % 26;
          const mx = m < 13 ? m * 2 - 2 : (25 - m) * 2 - 2;
          for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [1, 1]]) dot(m < 13 ? cv : back, mx + dx, (m < 13 ? y + 2 : y - 2) + dy, '#9A9AA0');
          break;
        }
        case 'flame':
          for (let i = 0; i < 3; i++) {
            const flick = (Math.floor(t * 12) + i) % 2;
            dot(cv, a.cx - 1 + i, a.cy + 7, '#F5BE2E');
            dot(cv, a.cx - 1 + i, a.cy + 8 + flick, '#F2832B');
            if (i === 1) dot(cv, a.cx, a.cy + 9 + flick, '#F2483B');
          }
          break;
        case 'shackle': {
          // A padlock's shackle over the ring, so the whole bot reads as a lock.
          const steel = '#8D8C94';
          for (let x = a.cx - 2; x <= a.cx + 3; x++) dot(cv, x, a.cy - 8, steel);
          for (const x of [a.cx - 2, a.cx + 3]) dot(cv, x, a.cy - 7, steel);
          break;
        }
        case 'swirl':
          for (let i = 0; i < 6; i++) {
            const u = frac(steps(t, 8) / 1.1 + i / 6);
            const ang = i * 60 + u * 220;
            const r = lerp(9, 4, u);
            dot(cv, rnd(a.cx + 0.5 + Math.sin(rad(ang)) * r), rnd(a.cy + 0.5 - Math.cos(rad(ang)) * r), INK);
          }
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// Cursor shares Grok's subscription, so it has no character of its own any
// more (the user, 2026-10-19): it draws Grok's ring.
CHARACTERS.cursor = CHARACTERS.grok;

// MARK: - Kimi Code · the pixel blob and its terminal
//
// Kimi Code's mascot in pixels: a round blue blob with tall white eyes,
// sitting on a navy terminal. The terminal does the talking — it types,
// scrolls, greps, asks [Y/n], fills a progress bar, turns red on errors —
// and the family's shared signs show the rest, but for a sleepy nose bubble
// and a question mark while it's locked. (v5: the droplet that floated by
// its shoulder is gone, the user's call, 2026-10-17; they kept those two.)

(() => {
  const BLUE = '#2B8BFF';
  const SHINE = '#7DB8FF';
  const NAVY = '#0B2C55';
  const CURSOR = '#3D97FF';
  const RED = '#C7372F';
  const WHITE = '#FFFFFF';
  const DULL = '#7C93AE';
  // A cell smaller than round 3 all round, with the terminal's bottom
  // corners rounded, so it fits inside the drawer's circle.
  const CX = 11, CY = 9, R0 = 5;
  const TERM = { x: 4, y: 13, w: 14, h: 5 };
  const CHEV = ['#.', '.#', '#.'];
  const MINI = ['.####.', '######', '#w##w#', '######', '.####.'];
  const pal = {
    main: BLUE, body: BLUE, dark: NAVY, light: SHINE, accent: BLUE,
    mini: (cv, x, y) => cv.glyph(MINI, x, y, BLUE, { w: WHITE }),
  };

  function blob(cv, p, c) {
    const dim = p.dim > 0.5;
    const body = dim ? DULL : BLUE;
    const rx = R0 * (1 + 0.22 * p.sq), ry = R0 * (1 - 0.3 * p.sq);
    const cx = CX + rnd(p.dx), bottom = CY + 5.6 + rnd(p.dy);
    const cy = bottom - ry;
    cv.shape((x, y) => ((x - cx - 0.5) / rx) ** 2 + ((y - cy - 0.5) / ry) ** 2 <= 1, cx - 7, Math.floor(cy - ry) - 1, 15, Math.ceil(ry * 2) + 2, body);
    if (!dim) { cv.put(cx - 3, rnd(cy - ry) + 2, SHINE); cv.put(cx - 2, rnd(cy - ry) + 1, SHINE); }
    const eyes = [[cx - 2, rnd(cy) - 2], [cx + 2, rnd(cy) - 2]];
    pxEyes(cv, eyes, c, p.eyes === 'open' ? 'wide' : p.eyes, WHITE);
    return { cx, cy, eyes, top: Math.floor(cy - ry), body };
  }

  // What the terminal shows, left to right after the prompt.
  function terminal(cv, p, c, t) {
    const red = p.term === 'red';
    const fill = red ? RED : p.dim > 0.5 ? '#33435A' : NAVY;
    cv.rect(TERM.x, TERM.y, TERM.w, TERM.h - 1, fill);
    cv.rect(TERM.x + 1, TERM.y + TERM.h - 1, TERM.w - 2, 1, fill);
    const row = TERM.y + 2, x0 = TERM.x + 2;
    const ink = p.dim > 0.5 ? '#9FB0C4' : WHITE;
    // Resting: a quiet prompt, dimmed, with no caret.
    if (!red && p.term !== 'yn' && p.term !== 'lock' && p.term !== 'check') cv.glyph(CHEV, x0, row - 1, p.term === 'rest' ? '#55708F' : ink);
    let caret = x0 + 3;
    const blink = frac(c.T / 1.05) < 0.55;
    switch (p.term) {
      case 'dots':
        for (let i = 0; i < 3; i++) cv.put(caret + i * 2, row, Math.floor(t * 5) % 3 === i ? ink : '#55708F');
        return;
      case 'type': {
        const n = Math.floor(frac(p.termU) * 4);
        for (let i = 0; i < n; i++) cv.rect(caret + i * 2, row, 1 + (i % 2), 1, ink);
        caret += n * 2;
        break;
      }
      case 'scroll':
        for (let j = 0; j < 3; j++) {
          const y = TERM.y + 1 + ((j + Math.floor(t * p.termU)) % 3);
          cv.rect(caret, y, 3 + ((j * 5 + Math.floor(t * p.termU)) % 6), 1, '#8FA8C8');
        }
        return;
      case 'grep': {
        const hit = Math.floor(t / 0.5) % 5;
        for (let i = 0; i < 5; i++) cv.rect(caret + i * 2, row, 1, 1, i === hit ? '#FFD84D' : '#55708F');
        return;
      }
      case 'bar': {
        const w = TERM.w - 7, fill = rnd(frac(t / 3.4) * w);
        cv.rect(caret, row, w, 1, '#3A5A80');
        cv.rect(caret, row, fill, 1, CURSOR);
        return;
      }
      case 'check':
        cv.glyph(['....#', '#..#.', '.##..'], TERM.x + 6, row - 1, '#5BE37D');
        return;
      case 'yn': {
        cv.glyph(['#.#...#.##.', '.#...#..#.#', '.#..#...#.#'], TERM.x + 1, row - 1, ink);
        caret = TERM.x + 12;
        break;
      }
      case 'lock':
        cv.glyph(['.#.', '###', '###'], TERM.x + 2, row - 1, ink);
        for (let i = 0; i < 3; i++) cv.put(TERM.x + 6 + i * 2, row, ink);
        caret = TERM.x + 12;
        break;
      case 'red':
        cv.glyph(['#.#', '.#.', '#.#'], TERM.x + 7, row - 1, WHITE);
        return;
      case 'lost':
        // Offline: a × where the caret was, blinking dim and bright.
        cv.glyph(['#.#', '.#.', '#.#'], caret, row - 1, Math.floor(t * 2) % 2 ? '#7FA3CC' : '#55708F');
        return;
    }
    if (p.term !== 'off' && p.term !== 'rest' && blink) cv.rect(caret, row + 1, 2, 1, CURSOR);
  }

  CHARACTERS.kimi = {
    name: 'Kimi Code',
    swatch: 0x2B8BFF,
    // v5: 'type' was terminal typing, so an idle Kimi looked busy. v5.1: a
    // jelly squish makes three again.
    fidgets: ['hop', 'squish', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, term: 'prompt', termU: 0, sign: '' },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      const nod = Math.floor(t * 4) % 2;
      switch (s) {
        case 'thinking': return { ...b, term: 'dots' };
        case 'streaming': return { ...b, dy: nod, term: 'type', termU: t / 3 };
        case 'reading': return { ...b, dy: 1, term: 'scroll', termU: 1.5, look: [0, 1] };
        case 'searching': return { ...b, dx: 0, term: 'grep', look: [Math.floor(t / 0.5) % 2 ? 1 : -1, 1] };
        case 'web': return { ...b, dy: 0, look: [1, -1] };
        case 'writing': return { ...b, dy: Math.floor(t * 8) % 2, term: 'type', termU: t / 1.4, look: [0, 1] };
        case 'running': return { ...b, dy: -(Math.floor(t * 10) % 2), term: 'scroll', termU: 6, eyes: 'focus' };
        case 'working': return { ...b, dy: nod, term: 'type', termU: t / 5 };
        case 'approving': return { ...b, term: 'yn', eyes: 'wide' };
        case 'compacting': return { ...b, term: 'bar' };
        case 'finished': return { ...b, term: 'check' };
        case 'failed': return { ...b, term: 'red' };
        case 'broken': return { ...b, dy: 1, term: 'red' };
        case 'offline': return { ...b, term: 'lost', prop: '' };
        // Squish: it squashes, springs up tall, wobbles and settles, smiling.
        case 'squish': {
          const k = Math.floor(t / 0.2);
          const sq = [0.8, -0.5, 0.45, -0.25, 0.15][k] ?? 0;
          return { ...b, sq, dy: sq < 0 ? -1 : 0, happy: t > 0.1 && t < 1.6 ? 1 : 0, gazeW: 0 };
        }
        case 'resting': return { ...b, dy: 1, term: 'off', sign: 'snot' };
        case 'locked': return { ...b, term: 'lock', sign: 'ask' };
        // Out of quota: sunk a cell into the terminal, a quiet prompt.
        case 'sleeping': return { ...b, dy: b.dy - 1, term: 'rest' };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => blob(g, q, c));
      const t = q.propT;
      const b = blob(cv, q, c);
      terminal(cv, q, c, t);
      const a = { mid: b.cx, top: b.top, left: b.cx - 6, right: b.cx + 6, ground: TERM.y + TERM.h, midY: rnd(b.cy), front: rnd(b.cy), eyes: b.eyes };
      // A nose bubble while it sleeps, a question mark while it's locked,
      // above the blob's right shoulder.
      if (q.sign === 'snot') {
        const r = 1 + Math.floor((Math.sin(c.T * 1.2) + 1) * 1.2);
        const x = b.cx + 5, y = b.cy + 1;
        fitDraw(cv, g => {
          g.shape((u, v) => Math.hypot(u - x - 0.5, v - y - 0.5) <= r + 0.3, x - r - 1, y - r - 1, r * 2 + 3, r * 2 + 3, '#CFE6FF');
          g.put(x - 1, y - r + 1, WHITE);
        }, null);
      } else if (q.sign === 'ask') fitDraw(cv, g => g.glyph(PIX.q, 13, 2, b.body));
      // The terminal already tells these phases; the shared signs show the
      // rest (the globe, the question mark, no Wi-Fi, the letter, z's).
      const told = ['think', 'bubble', 'page', 'mag', 'keys', 'run', 'hammer', 'sign', 'lock'];
      if (!told.includes(q.prop)) pxProp(cv, back, q, a, pal, c);
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// MARK: - DeepSeek · the pixel whale
//
// DeepSeek's whale as a pixel sprite in profile: a blue body with a pale
// belly, the logo's raised flukes and one slit eye. Calm and deep: it
// thinks in rising bubbles, hums notes to stream, pings sonar to search,
// surfs a pixel wave for the web, spouts its questions and leaps out of
// the water when it is done.

(() => {
  const BLUE = '#4D6BFE';
  const BELLY = '#E6ECFF';
  const FIN = '#3A56E8';
  const EYE = '#141C5C';
  const WATER = '#8EA6FF';
  const DULL = '#8D97BE';
  const DULL_BELLY = '#CBD0DE';
  // One row up from round 3, so the belly's lower corner stays inside the
  // drawer's circle.
  const X0 = 3, Y0 = 7;
  const MINI = ['.....#.#', '.####.#.', '#######.', '.wwww...'];
  const pal = {
    main: BLUE, body: BLUE, dark: EYE, light: WATER, accent: WATER,
    mini: (cv, x, y) => { cv.glyph(MINI, x, y, BLUE, { w: BELLY }); cv.put(x + 1, y + 1, EYE); },
  };

  function whale(cv, p, c) {
    const x0 = X0 + rnd(p.dx), y0 = Y0 + rnd(p.dy);
    const dim = p.dim > 0.5;
    const body = dim ? DULL : BLUE, belly = dim ? DULL_BELLY : BELLY;
    const ry = 4.3 * (1 - 0.3 * p.sq);
    const wag = rnd(p.wag);
    const seg = (x, y, ax, ay, bx, by, w) => {
      const vx = bx - ax, vy = by - ay, l = vx * vx + vy * vy;
      const k = clamp(((x - ax) * vx + (y - ay) * vy) / l);
      return Math.hypot(x - ax - vx * k, y - ay - vy * k) <= w;
    };
    const inside = (u, v) => {
      const x = u - x0, y = v - y0;
      const torso = ((x - 7) / 6.9) ** 2 + ((y - 6.4) / ry) ** 2 <= 1;
      const stock = seg(x, y, 12, 4.8, 14.3, 2 + wag * 0.5, 1.2);
      const flukes = ((x - 13.4) / 1.5) ** 2 + ((y - 1.3 - wag) / 0.85) ** 2 <= 1 || ((x - 15.9) / 1.4) ** 2 + ((y - 1.6 - wag) / 0.85) ** 2 <= 1;
      return torso || stock || flukes;
    };
    cv.shape(inside, x0 - 1, y0 - 2, 19, 14, (x, y) => {
      const u = x - x0 + 0.5, v = y - y0 + 0.5;
      return v > 7.2 + 0.014 * (u - 6) ** 2 && u < 12.4 ? belly : body;
    });
    // A little fin, and the eye near the front.
    cv.put(x0 + 8, y0 + 8, dim ? '#7782A8' : FIN);
    cv.put(x0 + 9, y0 + 9, dim ? '#7782A8' : FIN);
    const eye = [[x0 + 3, y0 + 4]];
    pxEyes(cv, eye, c, p.eyes, EYE);
    return { mid: x0 + 7, top: y0 + 2, left: x0, right: x0 + 14, ground: y0 + 12, midY: y0 + 6, front: y0 + 6, eyes: eye, hole: [x0 + 5, y0 + 2], mouth: [x0, y0 + 7], x0, y0 };
  }

  CHARACTERS.deepseek = {
    name: 'DeepSeek',
    swatch: 0x4D6BFE,
    // v5: 'spout' was sending's spout, so an idle whale looked like it was
    // sending; a quick wave of its flukes is idle play.
    fidgets: ['flukes', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, wag: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      const wag = Math.floor(T * 2) % 2 ? 1 : 0;
      switch (s) {
        case 'thinking': return { ...b, wag, prop: 'bubbles' };
        case 'streaming': return { ...b, wag, dy: 0, prop: 'song' };
        case 'searching': return { ...b, wag: Math.floor(t * 6) % 2, prop: 'sonar' };
        case 'web': return { ...b, dy: -2 + (Math.floor(t * 2.4) % 2), wag, prop: 'surf' };
        case 'writing': return { ...b, dy: -1, wag, prop: 'ink' };
        case 'running': return { ...b, dy: Math.floor(t * 6) % 2 ? 0 : -1, wag: Math.floor(t * 12) % 2, prop: 'speed' };
        case 'sending': return { ...b, wag, prop: 'spout', propU: frac(t / 2.2) };
        case 'asking': return { ...b, wag, dy: 0, prop: 'qspout' };
        case 'failed': return { ...b, dy: t < 0.6 ? 0 : 2, wag: 0, prop: 'sink' };
        case 'broken': return { ...b, wag: 0, prop: 'sink' };
        case 'resting': return { ...b, wag: 0, prop: 'zzb' };
        case 'offline': return { ...b, wag, prop: 'dive' };
        case 'locked': return { ...b, wag: 0, prop: 'cork' };
        case 'flukes': return { ...b, wag: t < 1.6 ? (Math.floor(t * 5) % 2 ? 2 : 0) : wag, happy: t > 0.2 && t < 1.9 ? 1 : 0, gazeW: 0 };
        // Out of quota: lower in the water, the tail swaying at a third the pace.
        case 'sleeping': return { ...b, wag: Math.floor(t / 1.5) % 2 };
        case 'finished': return { ...b, wag: Math.floor(t * 8) % 2, prop: t < 0.7 ? 'sparks' : 'splash' };
        default: return { ...b, wag };
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => whale(g, q, c));
      const t = q.propT;
      // Water, rings and drops show only where the circle allows.
      const dot = (x, y, color) => { if (safe(x, y)) cv.put(x, y, color); };
      if (q.prop === 'surf') {
        for (let x = 0; x < GRID; x++) dot(x, 18 + (Math.floor(x / 3 + t * 4) % 2), WATER);
      }
      const a = whale(cv, q, c);
      const [hx, hy] = a.hole;
      switch (q.prop) {
        case 'bubbles':
          for (let i = 0; i < 3; i++) {
            const u = frac(steps(t, 6) * 0.6 + i / 3);
            const y = hy - 2 - rnd(u * 6), x = hx + (i % 2);
            if (u < 0.5) dot(x, y, WATER); else fitDraw(cv, g => g.glyph(['.#.', '#.#', '.#.'], x - 1, y - 1, WATER));
          }
          break;
        case 'song': {
          // It hums: notes and sound bars over its head.
          for (let i = 0; i < 3; i++) {
            const h = 1 + (Math.floor(t * 8) + i * 2) % 3;
            fitDraw(cv, g => g.rect(hx - 4 + i * 2, hy - 1 - h, 1, h, WATER));
          }
          const u = frac(t / 1.8);
          fitDraw(cv, g => g.glyph(PIX.note, a.mid, a.top - 6 - rnd(u * 2), EYE));
          break;
        }
        case 'sonar': {
          // Pings ring up and out from the head, one arc wider at a time.
          const k = Math.floor(t * 4) % 4;
          for (let i = 0; i < Math.min(k, 3); i++) {
            const w = 3 + i * 2, x = hx - 1 - i, y = hy - 2 - i * 2;
            for (let j = 0; j < w; j++) dot(x + j, y + (j === 0 || j === w - 1 ? 1 : 0), WATER);
          }
          if (k === 3) dot(hx + 1, hy - 8, EYE);
          break;
        }
        case 'ink': {
          const len = Math.floor(frac(t / 1.6) * 12);
          for (let i = 0; i < len; i++) dot(a.x0 + 14 - i, a.ground - 1 + (i % 4 < 2 ? 0 : 1), EYE);
          break;
        }
        case 'speed':
          // Speed lines over its back.
          for (let i = 0; i < 3; i++) {
            const u = frac(steps(t, 10) * 1.5 + i / 3);
            fitDraw(cv, g => g.rect(a.right - 4 + rnd(u * 2), a.top - 1 + i * 2, 2, 1, WATER));
          }
          break;
        case 'spout': {
          const u = q.propU;
          const h = u < 0.3 ? rnd(u / 0.3 * 4) : u < 0.6 ? 4 : rnd((1 - (u - 0.6) / 0.3) * 4);
          for (let j = 1; j <= h; j++) dot(hx, hy - j, WATER);
          if (h >= 3) { dot(hx - 1, hy - h, WATER); dot(hx + 1, hy - h, WATER); dot(hx - 2, hy - h + 1, WATER); dot(hx + 2, hy - h + 1, WATER); }
          if (u > 0.45 && u < 0.95) {
            const v = (u - 0.45) / 0.5;
            fitDraw(cv, g => g.rect(rnd(lerp(hx, 15, v)), rnd(lerp(hy - 5, 3, v)), 2, 2, WATER));
          }
          break;
        }
        case 'qspout':
          for (let j = 1; j <= 2; j++) dot(hx, hy - j, WATER);
          fitDraw(cv, g => g.glyph(PIX.q, hx - 1, hy - 8 - (Math.floor(t * 2) % 2), WATER));
          break;
        case 'sink':
          for (let i = 0; i < 2; i++) {
            const u = frac(steps(t, 6) * 0.5 + i / 2);
            dot(hx + i, hy - 1 - rnd(u * 5), WATER);
          }
          if (q.cross > 0.5) pxProp(cv, back, { ...q, prop: 'smoke' }, a, pal, c);
          break;
        case 'zzb':
          for (let i = 0; i < 2; i++) {
            const u = frac(t / 2.4 + i / 2);
            if (u < 0.85) fitDraw(cv, g => g.glyph(i ? PIX.zs : PIX.z, hx + 2 + i * 2, hy - 3 - rnd(u * 4) - i * 2, WATER));
          }
          break;
        case 'dive': {
          // Under the surface: a wavy line over its back, bubbles going up.
          for (let x = 0; x < GRID; x++) {
            const y = a.top - 1 + ((Math.floor(x / 2) + Math.floor(t * 3)) % 2);
            if (safe(x, y) && !cv.has(x, y)) cv.put(x, y, WATER);
          }
          for (let i = 0; i < 2; i++) {
            const u = frac(t / 1.8 + i / 2);
            dot(hx + i * 2 - 1, a.top - 3 - rnd(u * 3), WATER);
          }
          break;
        }
        case 'cork': {
          // A cork in the blowhole; now and then it squeezes out a drop.
          cv.rect(hx, hy - 2, 2, 2, '#C8955A');
          cv.rect(hx, hy - 2, 2, 1, '#A4743F');
          if (frac(t / 1.6) < 0.35) { dot(hx - 1, hy - 1, WATER); dot(hx + 2, hy - 1, WATER); }
          break;
        }
        case 'splash':
          if (t < 1.5) for (const [dx, dy] of [[-3, 0], [3, 0], [-5, 1], [5, 1], [0, -1]]) dot(a.mid + dx + (Math.floor(t * 8) % 2), a.ground + dy - 2, WATER);
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();
