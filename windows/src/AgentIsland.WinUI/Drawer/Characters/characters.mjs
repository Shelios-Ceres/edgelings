// Agent Island drawer characters.
//
// One pixel bot per agent, after Clawd, Claude Code's own pixel mascot, each
// drawn from the idea behind that agent's icon: Clawd, the Codex cloud,
// Antigravity's rainbow "A", Grok's ring head, Cursor's cube, Kimi Code's
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
];

// States slow enough for the drawer's calm frame rate.
export const CALM = ['idle', 'listening', 'broken', 'offline', 'sleeping', 'locked'];

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
const REST_EYES = { look: [0, 0], gazeW: 1, blink: 1, open: 1, happy: 0, cross: 0, sad: 0, wide: 0, sleep: 0, wink: 0, squint: 0 };

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
        target = [lerp(target[0], g[0], pose.gazeW), lerp(target[1], g[1], pose.gazeW)];
      }
      const follow = 1 - Math.exp(-dt * 11);
      look[0] += (target[0] - look[0]) * follow;
      look[1] += (target[1] - look[1]) * follow;
    },
    svg(size = 44, id = 'b') {
      if (!pose) pose = blended();
      const q = T - blinkAt;
      const blink = pose.blink > 0.5 && q < 0.17 ? bump(q / 0.17) : 0;
      const eye = { look: [look[0], look[1]] };
      for (const key of EYE_KEYS) eye[key] = pose[key];
      eye.open = pose.open * (1 - blink);
      const c = { T, id, rim: rimWidth, eye, k };
      const body = def.draw(pose, c);
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
  hammerUp: ['###', '###', '.#.', '.#.', '.#.'],
  hammerDown: ['##...', '#####', '##...'],
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
    else if (mm === 'closed') cv.rect(side < 0 ? x - 1 : x, y + 1, 2, 1, color);
    else if (mm === 'blink' || mm === 'focus') cv.put(x, y + 1, color);
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
      return { armR: swing ? 'high' : 'out', eyes: 'focus', look: [1, 1], gazeW: 0, prop: 'hammer', propT: t, propU: swing };
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
    case 'sleeping':
      return { dy: 2, legs: 'tuck', armL: 'down', armR: 'down', sleep: 1, blink: 0, dim: 0.35, gazeW: 0, prop: 'zz', propT: T };
    case 'locked':
      return { armR: Math.floor(t * 2) % 3 === 0 ? 'high' : 'up', look: [1, -1], gazeW: 0, prop: 'lock', propT: t };
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
      const up = p.propU;
      float(g => {
        const hx = a.right - 1, hy = up ? top - 4 : top + 2;
        g.glyph(up ? PIX.hammerUp : PIX.hammerDown, hx, hy, GRAY);
        if (!up) for (const [dx, dy] of [[5, -1], [6, 1], [4, 2]]) g.put(hx + dx, hy + dy, Math.floor(t * 8) % 2 ? '#FFD36B' : WHITE);
      });
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
    const legs = { stand: [2, 2, 2, 2], walk1: [1, 2, 1, 2], walk2: [2, 1, 2, 1], tuck: [0, 0, 0, 0], jump: [1, 1, 1, 1] }[p.legs] ?? [2, 2, 2, 2];
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
      return basePose(s, t, T);
    },
    draw(p, c) {
      const color = p.dim > 0.5 ? DULL : ORANGE;
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => clawd(g, q, c, color));
      const a = clawd(cv, q, c, color);
      const body = { ...pal, body: color };
      if (q.prop === 'think') fitDraw(cv, g => g.glyph(SPINNER[Math.floor(q.propT * 7) % SPINNER.length], a.mid - 2, a.top - 7, ORANGE));
      else pxProp(cv, back, q, a, body, c);
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// MARK: - Codex · the pixel puff cloud
//
// The classic cloud: a big puff and a small one on a flat base, pale with
// the logo's grey line and the family's slit eyes. It floats, and sprouts
// little arms only to wave, cheer or type. It thinks under a ring of six
// dots, drizzles tokens as it streams, types a line between < and > as it
// writes, turns into a storm cloud when something fails and rains a little
// when it is sad. (Round 4, 2026-10-15: the user's pick over a cloud on
// feet; round 3's cloud with arms retired.) 14 cells wide so it and its
// weather fit inside the drawer's circle.

(() => {
  const FILL = '#F6F7F9', SHADE = '#E2E4EA', LINE = '#7D808B', INK = '#2E3038';
  const STORM = '#B4B7C0', STORM_SHADE = '#A3A6B0', STORM_LINE = '#6E717C';
  const GREEN = '#10A37F';
  const X0 = 4, Y0 = 6;
  // The six-dot ring, 4 × 3, over the big puff.
  const RING = [[1, 0], [2, 0], [3, 1], [2, 2], [1, 2], [0, 1]];
  const PUFF = [
    '.......####...',
    '......######..',
    '.##...######..',
    '####.########.',
    '##############',
    '##############',
    '##############',
    '.############.',
    '..##########..',
  ];
  const PUFF_SMALL = [
    '.....###..',
    '.##.#####.',
    '##########',
    '##########',
    '.########.',
  ];
  const MINI = ['...##..', '.##oo#.', '#ooooo#', '#oeoeo#', '.#####.'];
  const pal = {
    main: '#9EA1AB', body: FILL, dark: INK, light: '#B9BCC6', accent: INK, ink: LINE,
    mini: (cv, x, y) => cv.glyph(MINI, x, y, LINE, { o: FILL, e: INK }),
  };
  // Arms only when the pose reaches: out and down keep the cloud's outline.
  // They rise straight up, which is all the room the circle leaves.
  const ARMS = { up: [[0, 0], [0, -1]], high: [[0, 0], [0, -1], [0, -2]], wave: [[0, 0], [0, -1], [0, -2]], tap: [[0, 0]] };

  // Paints the rows, shade on the last two, and returns the cells.
  function paint(cv, rows, x0, y0, fill, shade) {
    const cells = [];
    rows.forEach((row, j) => [...row].forEach((ch, i) => {
      if (ch !== '#') return;
      cv.put(x0 + i, y0 + j, j >= rows.length - 2 ? shade : fill);
      cells.push([x0 + i, y0 + j]);
    }));
    return cells;
  }

  // One line round the cells, corners included: the shared outline steps
  // round corners only, which breaks the logo's stroke into dots on the
  // cloud's diagonal edges.
  function outline8(cv, list, color) {
    const mine = new Set(list.map(([x, y]) => `${x},${y}`));
    for (const [x, y] of list) for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
      if (!mine.has(`${x + dx},${y + dy}`) && !cv.has(x + dx, y + dy)) cv.put(x + dx, y + dy, color);
    }
  }

  function cloud(cv, p, c) {
    const stormy = p.dim > 0.5, small = p.sq > 0.5;
    const fill = stormy ? STORM : FILL;
    const rows = small ? PUFF_SMALL : PUFF;
    const x0 = X0 + rnd(p.dx) + (small ? 2 : 0), y0 = Y0 + rnd(p.dy) + (small ? 4 : 0);
    const cells = paint(cv, rows, x0, y0, fill, stormy ? STORM_SHADE : SHADE);
    if (!stormy && !small) for (const [dx, dy] of [[8, 1], [9, 1], [7, 2], [1, 3]]) cv.put(x0 + dx, y0 + dy, '#FFFFFF');
    const w = rows[0].length, ay = y0 + (small ? 3 : 6);
    for (const [side, mode] of [[-1, p.armL], [1, p.armR]]) {
      const sx = side < 0 ? x0 - 1 : x0 + w;
      for (const [dx, dy] of ARMS[mode] ?? []) {
        cv.put(sx + dx * side, ay + dy, fill);
        cells.push([sx + dx * side, ay + dy]);
      }
    }
    outline8(cv, cells, stormy ? STORM_LINE : LINE);
    const eyes = small ? [[x0 + 3, y0 + 2], [x0 + 6, y0 + 2]] : [[x0 + 4, y0 + 5], [x0 + 9, y0 + 5]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    return {
      x0, y0, w, eyes, hands: true,
      mid: x0 + w / 2, top: y0 - 1, left: x0, right: x0 + w - 1, ground: y0 + rows.length + 1,
      midY: y0 + 5, front: y0 + 7,
    };
  }

  CHARACTERS.codex = {
    name: 'Codex',
    swatch: 0x9EA1AB,
    fidgets: ['wave', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        case 'streaming': return { ...b, prop: 'tokens' };
        case 'writing': return { ...b, dy: -1, prop: 'braces' };
        case 'running': return { ...b, dy: Math.floor(t * 10) % 2 ? 0 : -1, prop: 'run' };
        case 'failed': return { ...b, prop: 'bolt' };
        case 'broken': return { ...b, prop: 'rain' };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => cloud(g, q, c));
      const a = cloud(cv, q, c);
      const t = q.propT;
      switch (q.prop) {
        case 'think': {
          const k = Math.floor(t * 8) % 6;
          fitDraw(cv, g => RING.forEach(([dx, dy], i) => g.put(a.x0 + 7 + dx, a.y0 - 4 + dy, i === k ? GREEN : i === (k + 5) % 6 ? '#7FCDB7' : '#C5C8D0')));
          break;
        }
        case 'tokens':
          // A drizzle of tokens from under the cloud.
          for (let i = 0; i < 3; i++) {
            const k = rnd(frac(steps(t, 8) * 0.8 + i / 3) * 2);
            fitDraw(cv, g => g.rect(a.x0 + 3 + i * 3, a.ground + k, 2, 1, i === 1 ? GREEN : '#9EA1AB'), null);
          }
          break;
        case 'braces':
          // A line types itself out between < and >, under the cloud.
          fitDraw(cv, g => {
            const y = a.ground;
            g.glyph(['.#', '#.', '.#'], a.x0 + 1, y, LINE);
            g.glyph(['#.', '.#', '#.'], a.x0 + 11, y, LINE);
            const len = Math.floor(frac(t / 2) * 8);
            g.rect(a.x0 + 3, y + 1, len, 1, INK);
            if (Math.floor(t * 3) % 2) g.rect(a.x0 + 3 + len, y, 1, 3, GREEN);
          }, null);
          break;
        case 'bolt':
          if (t > 0.3 && Math.floor(t * 6) % 3 !== 0) fitDraw(cv, g => g.glyph(['.##', '.#.', '##.', '.#.', '#..'], a.mid - 1, a.ground - 1, '#F2C230'), null);
          break;
        case 'rain':
          for (let i = 0; i < 4; i++) {
            const u = frac(steps(t, 8) * 1.2 + i / 4);
            fitDraw(cv, g => g.rect(a.left + 2 + i * 3, a.ground - 1 + rnd(u * 2), 1, 2, '#7FB4FF'), null);
          }
          break;
        case 'sparks':
          pxProp(cv, back, q, a, { ...pal, main: GREEN }, c);
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// MARK: - Antigravity · the pixel rainbow
//
// Antigravity's "A" as a pixel arch in rainbow stripes, hovering a couple
// of pixels above its own shadow. Weightless and bright: its stripes flow
// while it thinks, it throws confetti as it talks, sends rainbow Wi-Fi arcs
// up for the web, launches a little rocket to send, and when something
// fails the antigravity gives out and it drops to the ground, faded.

(() => {
  const BANDS = ['#F2483B', '#F2832B', '#F5BE2E', '#4DB86B', '#2FA3D6', '#3D7BF5'];
  const GRAY = '#9EA3AE';
  const DARK = '#1E2033';
  const SHADOW = '#2A2E3A';
  // 12 × 10 and hovering over a shadow on row 17, so it and its weather fit
  // inside the drawer's circle (round 3 drew 14 × 12 over row 21).
  const GROUND = 17, X0 = 5;
  const ARCH = [
    '.....##.....',
    '....####....',
    '...######...',
    '..########..',
    '..########..',
    '.##########.',
    '.##########.',
    '############',
    '####....####',
    '###......###',
  ];
  const SQUAT = ARCH.filter((_, j) => j !== 3 && j !== 5);
  const MINI = ['..##..', '.####.', '######', '##..##'];
  const ARCS = [['.###.', '#...#'], ['..###..', '.#...#.', '#.....#'], ['...###...', '.##...##.', '#.......#']];
  const pal = {
    main: BANDS[5], body: BANDS[2], dark: DARK, light: '#9FB8F5', accent: BANDS[0],
    mini: (cv, x, y) => MINI.forEach((row, j) => [...row].forEach((ch, i) => { if (ch === '#') cv.put(x + i, y + j, BANDS[Math.min(5, j + 1)]); })),
  };

  function arch(cv, shadowCv, p, c) {
    const rows = p.sq > 0.5 ? SQUAT : ARCH;
    const lift = rnd(p.lift);
    const x0 = X0 + rnd(p.dx);
    const bottom = GROUND - 1 - lift + rnd(p.dy);
    const y0 = bottom - rows.length + 1;
    const flow = rnd(p.flow);
    const gray = p.dim;
    rows.forEach((row, j) => [...row].forEach((ch, i) => {
      if (ch !== '#') return;
      // Lift a foot off the ground while walking.
      const foot = j >= rows.length - 2;
      if (foot && j === rows.length - 1 && ((p.legs === 'walk1' && i < 6) || (p.legs === 'walk2' && i >= 6))) return;
      const band = ((Math.floor(j * 6 / rows.length) + flow) % 6 + 6) % 6;
      cv.put(x0 + i, y0 + j, gray > 0.5 ? mixHex(BANDS[band], GRAY, 0.75) : BANDS[band]);
    }));
    // Its shadow: narrower and fainter the higher it floats.
    const w = clamp(10 - lift * 2, 4, 10);
    shadowCv.rect(x0 + 6 - w / 2, GROUND, w, 1, SHADOW);
    const eyeRow = y0 + Math.round(rows.length * 0.42);
    const eyes = [[x0 + 3, eyeRow], [x0 + 8, eyeRow]];
    pxEyes(cv, eyes, c, p.eyes, DARK);
    return { mid: x0 + 6, top: y0, left: x0, right: x0 + 12, ground: GROUND, midY: y0 + 5, front: y0 + 6, eyes, x0, y0 };
  }

  CHARACTERS.antigravity = {
    name: 'Antigravity',
    swatch: 0x3D7BF5,
    fidgets: ['hop', 'shimmer', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, lift: 2, flow: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      const hover = frac(T / 1.6) < 0.5 ? 2 : 3;
      switch (s) {
        case 'thinking': return { ...b, lift: 3, flow: Math.floor(t * 6), prop: 'think' };
        case 'streaming': return { ...b, lift: hover, prop: 'confetti' };
        case 'web': return { ...b, lift: hover, prop: 'arcs' };
        case 'sending': return { ...b, lift: hover, prop: 'rocket', propU: frac(t / 2.2) };
        case 'running': return { ...b, lift: 0, dy: 0 };
        case 'searching': return { ...b, lift: hover };
        case 'finished': return { ...b, lift: 2 - rnd(b.dy), dy: 0, flow: Math.floor(t * 8), prop: 'sparks' };
        case 'asking': return { ...b, lift: 2 - rnd(b.dy), dy: 0 };
        case 'failed': return { ...b, lift: t < 0.3 ? 2 - Math.floor(t * 10) : 0 };
        case 'broken': return { ...b, lift: frac(t / 4.5) < 0.12 ? 1 : 0, dy: 0 };
        case 'sleeping': return { ...b, lift: 0, dy: 0 };
        case 'offline': return { ...b, lift: hash(Math.floor(T * 6)) > 0.7 ? 1 : 2, dim: hash(Math.floor(T * 6) + 1) > 0.6 ? 1 : 0 };
        case 'shimmer': return { ...b, lift: hover, flow: Math.floor(t * 6), happy: 1, gazeW: 0 };
        case 'hop': return { ...b, lift: 2 - rnd(b.dy), dy: 0 };
        default: return { ...b, lift: hover, dy: 0 };
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas(), shade = canvas();
      const q = settle(p, (g, q) => arch(g, canvas(), q, c));
      const a = arch(cv, shade, q, c);
      const t = q.propT;
      // Single-cell particles only show where the circle allows.
      const dot = (x, y, color) => { if (safe(x, y)) cv.put(x, y, color); };
      switch (q.prop) {
        case 'think':
          if (Math.floor(t * 4) % 2) fitDraw(cv, g => g.glyph(PIX.spark, a.mid - 2, a.top - 4, BANDS[Math.floor(t * 4) % 6]));
          break;
        case 'confetti':
          for (let i = 0; i < 5; i++) {
            const u = frac(steps(t, 8) * 0.9 + i / 5);
            const dir = i % 2 ? 1 : -1;
            dot(a.mid + dir * rnd(u * (2 + i)), a.top - 1 - rnd(u * 4) + rnd(u * u * 3), BANDS[i % 6]);
          }
          break;
        case 'arcs': {
          // Two Wi-Fi arcs over the apex, the room the circle leaves.
          const k = Math.floor(t * 3) % 3;
          for (let i = 0; i < k; i++) {
            const g = ARCS[i];
            fitDraw(cv, h => h.glyph(g, a.mid - 1 - (g[0].length >> 1), a.top - 1 - i * 2 - g.length + 1, BANDS[i * 2]));
          }
          break;
        }
        case 'rocket': {
          const u = q.propU;
          if (u > 0.25 && u < 0.6) {
            const y = rnd(lerp(a.top - 2, 2, (u - 0.25) / 0.35));
            fitDraw(cv, g => { g.rect(a.mid - 1, y, 2, 2, BANDS[2]); g.put(a.mid - 1, y + 2, BANDS[1]); g.put(a.mid, y + 3, BANDS[0]); });
          } else if (u >= 0.6 && u < 0.9) {
            const r = rnd((u - 0.6) / 0.3 * 3) + 1;
            [[-r, 0], [r, 0], [0, -r], [0, r]].forEach(([dx, dy], i) => dot(a.mid + dx, 4 + dy, BANDS[[0, 2, 3, 5][i]]));
          }
          break;
        }
        case 'sparks':
          pxProp(cv, back, q, a, { ...pal, main: BANDS[0] }, c);
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(shade, { ...c, rim: 0 }, 0.22) + pixels(back, c, 0.7) + pixels(cv, c);
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
      else if (mood === 'closed') cv.rect(x - (right ? 0 : 1), top + 3, 3, 1, ink);
      else if (mood === 'blink') cv.rect(x, top + 2, 2, 1, ink);
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
        case 'offline': return { ...b, dashed: 1 };
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

// MARK: - Cursor · the pixel cube
//
// Cursor's cube as classic 2:1 pixel isometry: a graphite box with a white
// pointer arrow on its right face, slit eyes on its left and two tiny legs.
// Crisp and precise; its signs are the cursor shapes everyone knows: the
// hourglass while it thinks, Tab's ghost text, the I-beam, the help arrow,
// the busy ring and the not-allowed circle.

(() => {
  const TOP = '#D4D4DC', EDGE = '#F4F4F8', LEFT = '#5E5E6A', RIGHT = '#30303A';
  const DIM_TOP = '#A9A9B0', DIM_LEFT = '#77777E', DIM_RIGHT = '#55555C';
  const WHITE = '#F2F2F5';
  const DARK = '#1A1A20';
  const RED = '#D9534F';
  // One row up from round 3, so the idle bob keeps the legs inside the circle.
  const X0 = 5, Y0 = 5;
  const ARROW = ['#...', '##..', '###.', '####', '..#.', '...#'];
  // Compact signs, sized for the room the circle leaves round the cube.
  const HOURGLASS = [
    ['#####', '.#s#.', '..#..', '.#.#.', '#####'],
    ['#####', '.#.#.', '..s..', '.#.#.', '#####'],
    ['#####', '.#.#.', '..#..', '.#s#.', '#####'],
  ];
  const NOPE = ['.###.', '#..##', '#.#.#', '##..#', '.###.'];
  const IBEAM = ['###', '.#.', '.#.', '###'];
  const MINI = ['..tt..', 'tttttt', 'llttrr', 'lllrrr', 'lllrrr', '.llrr.'];
  const pal = {
    main: LEFT, body: LEFT, dark: DARK, light: '#C4C4CE', accent: DARK,
    mini: (cv, x, y) => cv.glyph(MINI, x, y, LEFT, { t: TOP, l: LEFT, r: RIGHT }),
  };

  // A crisp 2:1 isometric cube, hand-drawn: T top, L left, R right face.
  const cubeRows = side => [
    '.....TT.....',
    '...TTTTTT...',
    '.TTTTTTTTTT.',
    'LETTTTTTTTER',
    'LLLEETTEERRR',
    'LLLLLEERRRRR',
    ...Array.from({ length: side }, () => 'LLLLLLRRRRRR'),
    '.LLLLLRRRRR.',
    '...LLLRRR...',
    '.....LR.....',
  ];

  function cube(cv, p, c) {
    const side = p.sq > 0.5 ? 1 : 4;
    const x0 = X0 + rnd(p.dx), y0 = Y0 + rnd(p.dy) + (4 - side);
    const dim = p.dim > 0.5;
    const rows = cubeRows(side);
    cv.glyph(rows, x0, y0, TOP, { T: dim ? DIM_TOP : TOP, E: dim ? DIM_TOP : EDGE, L: dim ? DIM_LEFT : LEFT, R: dim ? DIM_RIGHT : RIGHT });
    // A dark outline keeps the pale top face crisp on pale walls.
    const cells = [];
    rows.forEach((row, j) => [...row].forEach((ch, i) => { if (ch !== '.') cells.push([x0 + i, y0 + j]); }));
    cv.outline(cells, DARK);
    // The pointer on the right face, and legs under the bottom edges.
    if (p.face !== 'off') cv.glyph(side > 2 ? ARROW : ARROW.slice(0, 4), x0 + 7, y0 + 5, dim ? '#9A9AA2' : WHITE);
    const legs = { stand: [2, 2], walk1: [1, 2], walk2: [2, 1], jump: [1, 1], tuck: [0, 0] }[p.legs] ?? [2, 2];
    const bottom = y0 + 8 + side;
    if (legs[0]) cv.rect(x0 + 3, bottom, 1, legs[0], DARK);
    if (legs[1]) cv.rect(x0 + 8, bottom, 1, legs[1], DARK);
    const arm = (s, mode) => {
      const x = s < 0 ? x0 - 1 : x0 + 12;
      const dy = { out: 0, up: -1, high: -2, down: 1, wave: -2, tap: 1 }[mode] ?? 0;
      const y = y0 + 6 + dy;
      const color = s < 0 ? (dim ? DIM_LEFT : LEFT) : (dim ? DIM_RIGHT : RIGHT);
      cv.put(x, y, color);
      if (mode === 'high' || mode === 'wave') cv.put(x + (mode === 'wave' ? s : 0), y - 1, color);
    };
    arm(-1, p.armL);
    arm(1, p.armR);
    // Slit eyes painted on the left face, stepping down its slope.
    const eyes = [[x0 + 1, y0 + 6], [x0 + 3, y0 + 7]];
    pxEyes(cv, eyes, c, p.eyes, dim ? '#C9C9CF' : WHITE);
    return { mid: x0 + 6, top: y0, left: x0, right: x0 + 12, ground: bottom + 2, midY: y0 + 6, front: y0 + 8, pageX: x0 + 4, eyes, x0, y0, hands: false };
  }

  CHARACTERS.cursor = {
    name: 'Cursor',
    swatch: 0x45454F,
    fidgets: ['hop', 'click', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, face: 'on' },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        case 'streaming': return { ...b, prop: 'ghost', propU: frac(t / 1.8) };
        case 'writing': return { ...b, prop: 'ibeam' };
        case 'controlling': return { ...b, face: frac(t / 1.2) > 0.1 ? 'off' : 'on' };
        case 'asking': return { ...b, prop: 'help' };
        case 'failed': return { ...b, prop: 'nope' };
        case 'broken': return { ...b, prop: 'nope' };
        case 'offline': return { ...b, prop: 'busy' };
        case 'listening': return { ...b, prop: 'caret' };
        case 'click': return { ...basePose('controlling', t, T), face: 'off' };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => cube(g, q, c));
      const a = cube(cv, q, c);
      const t = q.propT;
      switch (q.prop) {
        case 'think':
          fitDraw(cv, g => g.glyph(HOURGLASS[Math.floor(t * 2.5) % 3], a.mid + 3, a.top - 2, DARK, { s: '#F5BE2E' }));
          break;
        case 'ghost': {
          // Tab's grey ghost text runs out over the cube, then is taken.
          const u = q.propU, len = rnd(clamp(u / 0.55) * 10);
          fitDraw(cv, g => {
            const x = a.mid - 5, y = a.top - 2;
            if (u < 0.62) for (let i = 0; i < len; i += 2) g.put(x + i, y, '#A5A5B0');
            else g.rect(x, y, 10, 1, DARK);
            if (u > 0.6 && u < 0.72) g.rect(x, y - 1, 10, 1, '#FFFFFF');
          });
          break;
        }
        case 'ibeam': {
          const len = Math.floor(frac(t / 2) * 9);
          fitDraw(cv, g => {
            const x = a.mid - 5, y = a.top - 2;
            g.rect(x, y, len, 1, DARK);
            if (Math.floor(t * 3) % 2) g.glyph(IBEAM, x - 1 + len, y - 2, DARK);
          });
          break;
        }
        case 'help':
          fitDraw(cv, g => g.glyph(PIX.arrow, a.left, a.top - 2, DARK, { w: '#FFFFFF' }));
          fitDraw(cv, g => g.glyph(PIX.q, a.right - 3, a.top - 2, DARK));
          break;
        case 'nope':
          fitDraw(cv, g => g.glyph(NOPE, a.right - 3, a.top - 2, RED));
          if (q.cross > 0.5) pxProp(cv, back, { ...q, prop: 'smoke' }, a, pal, c);
          break;
        case 'busy': {
          const ring = [[1, 0], [2, 0], [3, 0], [4, 1], [4, 2], [4, 3], [3, 4], [2, 4], [1, 4], [0, 3], [0, 2], [0, 1]];
          const k = Math.floor(t * 10) % ring.length;
          fitDraw(cv, g => ring.forEach(([dx, dy], i) => g.put(a.right - 3 + dx, a.top - 2 + dy, (i - k + ring.length) % ring.length < 3 ? '#3D7BF5' : '#C9CBD3')));
          break;
        }
        case 'caret':
          if (frac(c.T / 1.06) < 0.55) fitDraw(cv, g => g.glyph(IBEAM, a.right + 1, a.midY - 2, DARK));
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  };
})();

// MARK: - Kimi Code · the pixel blob and its terminal
//
// Kimi Code's mascot in pixels: a round blue blob with tall white eyes,
// sitting on a navy terminal, a little droplet floating by. The terminal
// does the talking — it types, scrolls, greps, asks [Y/n], fills a
// progress bar, turns red on errors — and the droplet becomes thought
// bubbles, a globe, a speech bubble, a sweat drop or a sleepy nose bubble.

(() => {
  const BLUE = '#2B8BFF';
  const SHINE = '#7DB8FF';
  const NAVY = '#0B2C55';
  const CURSOR = '#3D97FF';
  const RED = '#C7372F';
  const WHITE = '#FFFFFF';
  const DULL = '#7C93AE';
  // A cell smaller than round 3 all round, with the terminal's bottom
  // corners rounded, so it and its droplet fit inside the drawer's circle.
  const CX = 11, CY = 9, R0 = 5;
  const TERM = { x: 4, y: 13, w: 14, h: 5 };
  const DROP = ['.##', '###', '#..'];
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
    if (!red && p.term !== 'yn' && p.term !== 'lock' && p.term !== 'check') cv.glyph(CHEV, x0, row - 1, ink);
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
    }
    if (p.term !== 'off' && blink) cv.rect(caret, row + 1, 2, 1, CURSOR);
  }

  CHARACTERS.kimi = {
    name: 'Kimi Code',
    swatch: 0x2B8BFF,
    fidgets: ['hop', 'type', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, term: 'prompt', termU: 0, drop: 'drop' },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      const nod = Math.floor(t * 4) % 2;
      switch (s) {
        case 'thinking': return { ...b, term: 'dots', drop: 'think' };
        case 'streaming': return { ...b, dy: nod, term: 'type', termU: t / 3 };
        case 'reading': return { ...b, dy: 1, term: 'scroll', termU: 1.5, look: [0, 1] };
        case 'searching': return { ...b, dx: 0, term: 'grep', look: [Math.floor(t / 0.5) % 2 ? 1 : -1, 1] };
        case 'web': return { ...b, dy: 0, drop: 'globe', look: [1, -1] };
        case 'writing': return { ...b, dy: Math.floor(t * 8) % 2, term: 'type', termU: t / 1.4, look: [0, 1] };
        case 'running': return { ...b, dy: -(Math.floor(t * 10) % 2), term: 'scroll', termU: 6, drop: 'sweat', eyes: 'focus' };
        case 'working': return { ...b, dy: nod, term: 'type', termU: t / 5 };
        case 'approving': return { ...b, term: 'yn', eyes: 'wide' };
        case 'compacting': return { ...b, term: 'bar' };
        case 'finished': return { ...b, term: 'check' };
        case 'failed': return { ...b, term: 'red' };
        case 'broken': return { ...b, dy: 1, term: 'red' };
        case 'offline': return { ...b, term: 'dots', drop: 'gray' };
        case 'sleeping': return { ...b, dy: 1, term: 'off', drop: 'snot' };
        case 'locked': return { ...b, term: 'lock', drop: 'ask' };
        case 'asking': return { ...b, drop: 'ask' };
        case 'sending': return { ...b, drop: 'fly', propU: frac(t / 2.2) };
        case 'spawning': return { ...b, drop: 'none' };
        case 'type': return { ...b, term: 'type', termU: t < 1.5 ? t / 2.4 : Math.max(0, (3 - t) / 2.4) };
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
      // The droplet and what it turns into, just above the blob's right
      // shoulder: the room the circle leaves. It shimmers instead of bobbing.
      const dx = 13, dy = 3;
      const drop = color => fitDraw(cv, g => {
        g.glyph(DROP, dx, dy, color);
        if (frac(c.T / 1.9) < 0.5) g.put(dx + 2, dy, SHINE);
      });
      switch (q.drop) {
        case 'drop': drop(b.body); break;
        case 'gray':
          drop('#9AA6B2');
          fitDraw(cv, g => g.glyph(PIX.excl, dx + 4, dy - 1, '#D9534F'));
          break;
        case 'think':
          for (let i = 0; i < 3; i++) {
            const u = frac(steps(t, 6) * 0.7 + i / 3);
            const size = 1 + rnd(u * 1.5);
            fitDraw(cv, g => g.rect(13 + rnd(u * 3), 6 - rnd(u * 4), size, size, b.body));
          }
          break;
        case 'globe': fitDraw(cv, g => g.glyph(PIX.globe[Math.floor(t * 4) % 3], dx, dy - 1, SHINE)); break;
        case 'sweat': {
          const u = frac(steps(t, 8) / 1.2);
          fitDraw(cv, g => g.glyph(['#', '#'], b.cx + 6, b.top + 2 + rnd(u * 5), '#BFE0FF'), null);
          break;
        }
        case 'snot': {
          const r = 1 + Math.floor((Math.sin(c.T * 1.2) + 1) * 1.2);
          const x = b.cx + 5, y = b.cy + 1;
          fitDraw(cv, g => {
            g.shape((u, v) => Math.hypot(u - x - 0.5, v - y - 0.5) <= r + 0.3, x - r - 1, y - r - 1, r * 2 + 3, r * 2 + 3, '#CFE6FF');
            g.put(x - 1, y - r + 1, WHITE);
          }, null);
          break;
        }
        case 'ask':
          fitDraw(cv, g => g.glyph(PIX.q, dx, dy - 1, b.body));
          break;
        case 'fly': {
          // The droplet folds into a note, flies up over the head and pops.
          const u = q.propU;
          if (u < 0.3) { drop(b.body); break; }
          const v = clamp((u - 0.3) / 0.5);
          if (v < 1) {
            const x = rnd(lerp(dx, a.mid - 3, v)), y = rnd(lerp(dy, 1, v));
            fitDraw(cv, g => { g.rect(x, y, 5, 3, b.body); for (let i = 0; i < 2; i++) g.put(x + 1 + i * 2, y + 1, WHITE); });
          } else if (u < 0.88) fitDraw(cv, g => g.glyph(PIX.spark, a.mid - 1, 2, SHINE));
          if (u > 0.85) drop(b.body);
          break;
        }
      }
      // The terminal and the droplet already tell these phases.
      const told = ['think', 'bubble', 'page', 'mag', 'globe', 'keys', 'run', 'hammer', 'q', 'sign', 'nowifi', 'lock', 'mail'];
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
    fidgets: ['spout', 'hop', 'peek'],
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
        case 'sleeping': return { ...b, wag: 0, prop: 'zzb' };
        case 'spout': return { ...b, wag, prop: 'spout', propU: clamp(t / 2.2) * 0.85, happy: 1, gazeW: 0 };
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
