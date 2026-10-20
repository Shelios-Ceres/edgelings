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

// Looks (v5.2): an agent can wear more than one character in the same style,
// picked by the user (2026-10-20). A kind's first look is its default, the
// one CHARACTERS holds; the host passes the picked look's id to createBot
// and to the lookups below. An unknown or missing id draws the default.
const LOOK_DEFS = {};
function addLook(kind, id, name, def, first = false) {
  const list = (LOOK_DEFS[kind] ??= []);
  if (first) list.unshift({ id, name, def }); else list.push({ id, name, def });
}
const defOf = (kind, look) => (look && LOOK_DEFS[kind]?.find(l => l.id === look)?.def) || CHARACTERS[kind] || CHARACTERS.claude;

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

export function createBot(kind, { seed = 1, tempo = 1, rim = true, look: lookId = '' } = {}) {
  const def = defOf(kind, lookId);
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

export const fidgetsOf = (kind, look) => defOf(kind, look).fidgets;
export const cycleOf = (kind, state, look) => defOf(kind, look).cycle?.[state] ?? 3;
export const settleOf = state => SETTLE[state] ?? null;
export const swatchOf = (kind, look) => defOf(kind, look).swatch;
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

// MARK: - Look kit (v5.2)
//
// What the 2026-10-20 looks share: letter sprites, block eyes for screens
// and dark faces, and a small builder that registers a look with its own
// poses and props on top of the family's base poses and shared props.

// Rows of letters, each mapped through `map`; '.' and unmapped letters stay empty.
function art(cv, rows, x, y, map) {
  rows.forEach((row, j) => [...row].forEach((ch, i) => { if (map[ch]) cv.put(x + i, y + j, map[ch]); }));
}

// The eye mood a pose asks for, in the kit's order.
function moodOf(c, mode) {
  const e = c.eye;
  return e.cross > 0.5 ? 'x' : e.happy > 0.5 ? 'happy' : e.sleep > 0.5 ? 'closed' : e.open < 0.45 ? 'blink' : mode;
}

// Block eyes, `w` wide and three tall. They glance a cell toward the
// pointer and change shape with the mood.
function blockEyes(cv, at, c, mode, color, w = 2) {
  const e = c.eye;
  const lx = clamp(rnd(e.look[0] * 1.3), -1, 1), ly = clamp(rnd(e.look[1] * 1.2), -1, 1);
  const m = moodOf(c, mode);
  at.forEach(([x0, y0], i) => {
    const x = x0 + lx, y = y0 + ly, right = i === 1;
    const mm = right && e.wink > 0.5 ? 'happy' : m;
    if (mm === 'x') { for (const [dx, dy] of [[0, 0], [w - 1, 0], [0, 2], [w - 1, 2]]) cv.put(x + dx, y + dy, color); cv.rect(x, y + 1, w, 1, color); }
    else if (mm === 'happy') { cv.rect(x, y + 1, w, 1, color); cv.put(x - 1, y + 2, color); cv.put(x + w, y + 2, color); }
    else if (mm === 'content') { cv.rect(x, y + 2, w, 1, color); cv.put(x - 1, y + 1, color); cv.put(x + w, y + 1, color); }
    else if (mm === 'closed') cv.rect(x - (right ? 0 : 1), y + 2, w + 1, 1, color);
    else if (mm === 'blink' || mm === 'rest') cv.rect(x, y + 2, w, 1, color);
    else if (mm === 'focus') cv.rect(x, y + 1, w, 2, color);
    else if (mm === 'wide') cv.rect(x, y - 1, w, 4, color);
    else if (mm === 'sad') { cv.rect(x, y + 1, w, 2, color); cv.put(right ? x : x + w - 1, y, color); }
    else cv.rect(x, y, w, 3, color);
  });
}

// Registers a look: `poses(s, t, T, b)` returns its own pose for a state or
// nothing (the base pose b stands); `props(cv, back, q, a, c)` returns true
// for a prop it drew itself, otherwise the family's shared props draw it.
function lookBot(kind, id, name, swatch, body, pal, { poses, props, fidgets = ['hop', 'peek'], rest = {} } = {}) {
  addLook(kind, id, name, {
    name: CHARACTERS[kind].name, swatch, fidgets, cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, ...rest },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      return (poses && poses(s, t, T, b)) ?? b;
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => body(g, q, c));
      const a = body(cv, q, c);
      if (!(props && props(cv, back, q, a, c))) pxProp(cv, back, q, a, pal, c);
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
}

// MARK: - Codex · the pixel puff cloud
//
// The classic cloud: a big puff and a small one on a flat base, pale with
// the logo's grey line and the family's slit eyes. It floats, and sprouts
// little arms only to wave, cheer or type. It thinks under a ring of six
// dots, drizzles tokens as it streams, types a line between < and > as it
// writes, turns into a storm cloud when something fails and rains a little
// when it is sad. (Round 4, 2026-10-15: the user's pick over a cloud on
// feet; round 3's cloud with arms retired.) 14 cells wide so it and its
// weather fit inside the drawer's circle. (v5.2: a look Codex can wear
// again. Offline it thins out, its line breaking up; locked it freezes,
// icicles under it.)

(() => {
  const FILL = '#F6F7F9', SHADE = '#E2E4EA', LINE = '#7D808B', INK = '#2E3038';
  const STORM = '#B4B7C0', STORM_SHADE = '#A3A6B0', STORM_LINE = '#6E717C';
  const GREEN = '#10A37F';
  const ICE = '#E3EEFB', ICE_SHADE = '#C9DBF2', ICE_LINE = '#6F93BF';
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
    const stormy = p.dim > 0.5, small = p.sq > 0.5, frozen = p.frozen > 0.5;
    const fill = frozen ? ICE : stormy ? STORM : FILL;
    const rows = small ? PUFF_SMALL : PUFF;
    const x0 = X0 + rnd(p.dx) + (small ? 2 : 0), y0 = Y0 + rnd(p.dy) + (small ? 4 : 0);
    const cells = paint(cv, rows, x0, y0, fill, frozen ? ICE_SHADE : stormy ? STORM_SHADE : SHADE);
    if (!stormy && !small) for (const [dx, dy] of [[8, 1], [9, 1], [7, 2], [1, 3]]) cv.put(x0 + dx, y0 + dy, '#FFFFFF');
    const w = rows[0].length, ay = y0 + (small ? 3 : 6);
    for (const [side, mode] of [[-1, p.armL], [1, p.armR]]) {
      const sx = side < 0 ? x0 - 1 : x0 + w;
      for (const [dx, dy] of ARMS[mode] ?? []) {
        cv.put(sx + dx * side, ay + dy, fill);
        cells.push([sx + dx * side, ay + dy]);
      }
    }
    const line = frozen ? ICE_LINE : stormy ? STORM_LINE : LINE;
    outline8(cv, cells, line);
    // Offline: the line breaks up and wanders, the cloud thinning out.
    if (p.thin > 0.5) {
      const tick = Math.floor(c.T * 3);
      for (const [k, color] of [...cv.cells]) {
        const [x, y] = k.split(',').map(Number);
        if (color === line && (x + y + tick) % 3 === 0) cv.clear(x, y);
      }
    }
    const eyes = small ? [[x0 + 3, y0 + 2], [x0 + 6, y0 + 2]] : [[x0 + 4, y0 + 5], [x0 + 9, y0 + 5]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    return {
      x0, y0, w, eyes, hands: true,
      mid: x0 + w / 2, top: y0 - 1, left: x0, right: x0 + w - 1, ground: y0 + rows.length + 1,
      midY: y0 + 5, front: y0 + 7,
    };
  }

  addLook('codex', 'puff', 'Puff cloud', {
    name: 'Codex',
    swatch: 0xA2A5AF,
    fidgets: ['wave', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, thin: 0, frozen: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        case 'streaming': return { ...b, prop: 'tokens' };
        case 'writing': return { ...b, dy: -1, prop: 'braces' };
        case 'running': return { ...b, dy: Math.floor(t * 10) % 2 ? 0 : -1, prop: 'run' };
        case 'failed': return { ...b, prop: 'bolt' };
        case 'broken': return { ...b, prop: 'rain' };
        case 'offline': return { ...b, prop: '', thin: 1 };
        case 'locked': return { ...b, prop: 'ice', frozen: 1, look: [0, -1] };
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
        case 'ice':
          // Icicles under the frozen cloud, one dripping now and then.
          for (const [dx, h] of [[3, 3], [6, 2], [10, 3]]) {
            const drip = dx === 6 && frac(t / 2.2) > 0.7;
            fitDraw(cv, g => { g.rect(a.x0 + dx, a.ground - 1, 1, h, ICE_LINE); if (drip) g.put(a.x0 + dx, a.ground + 2, '#7FB4FF'); }, null);
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
  });
})();

// MARK: - Codex · the cloud with arms (round 3, refitted)
//
// A gray-white pixel cloud with tiny arms: the OpenAI blossom's soft,
// six-fold calm turned into weather. It thinks under a ring of six dots,
// sends tokens out a pixel at a time, writes between braces, turns into a
// storm cloud when something fails and rains a little when it is sad.
// (v5.2: round 3's Codex, a look Codex can wear again, refitted into the
// drawer's circle: two cells narrower, its arms always out, and its weather
// slid inside. Offline its ring of dots stalls grey, one blinking red;
// locked it holds a padlock out in front.)

(() => {
  const BODY = '#ECEDF1', SHADE = '#CDD0D8', LINE = '#7A7D88';
  const STORM = '#8E919C', STORM_SHADE = '#767985';
  const DARK = '#26272D', GREEN = '#10A37F';
  const X0 = 4, Y0 = 6;
  const HEX = [[1, 0], [3, 0], [4, 2], [3, 4], [1, 4], [0, 2]];
  const MINI = ['...ooo..', '.oo###o.', 'o######o', 'o#e##e#o', '.oooooo.'];
  const pal = {
    main: '#B9BCC6', body: BODY, dark: DARK, light: '#B9BCC6', accent: DARK, ink: LINE,
    mini: (cv, x, y) => cv.glyph(MINI, x, y, BODY, { o: LINE, e: DARK }),
  };
  // Hand-drawn so the three bumps keep their dips at this size.
  const CLOUD = [
    '.....###......',
    '....#####.....',
    '.##.#####.##..',
    '.############.',
    '##############',
    '##############',
    '##############',
    '.############.',
    '..##########..',
  ];
  const SMALL = [
    '....###...',
    '.##.####..',
    '##########',
    '##########',
    '.########.',
    '..######..',
  ];

  function cloud(cv, p, c) {
    const small = p.sq > 0.5;
    const rows = small ? SMALL : CLOUD;
    const x0 = X0 + rnd(p.dx) + (small ? 2 : 0), y0 = Y0 + rnd(p.dy) + (small ? 2 : 0);
    const stormy = p.dim > 0.5;
    const shade = stormy ? STORM_SHADE : SHADE, fill = stormy ? STORM : BODY;
    const cells = [];
    rows.forEach((row, j) => [...row].forEach((ch, i) => {
      if (ch !== '#') return;
      cv.put(x0 + i, y0 + j, j >= rows.length - 2 ? shade : fill);
      cells.push([x0 + i, y0 + j]);
    }));
    if (!stormy) for (const [dx, dy] of small ? [[5, 1]] : [[5, 1], [6, 1], [2, 3]]) cv.put(x0 + dx, y0 + dy, '#FAFAFC');
    // Arm nubs, then the outline round body and arms together.
    const ay = y0 + (small ? 3 : 5);
    const arm = (side, mode) => {
      const left = cells.reduce((m, [x, y]) => y === ay ? Math.min(m, x) : m, 99);
      const right = cells.reduce((m, [x, y]) => y === ay ? Math.max(m, x) : m, -99);
      const ax = side < 0 ? left - 1 : right + 1;
      const list = mode === 'up' ? [[ax, ay - 1]] : mode === 'high' || mode === 'wave' ? [[ax, ay - 1], [ax, ay - 2]]
        : mode === 'down' || mode === 'tap' ? [[ax, ay + 1]] : [[ax, ay]];
      for (const [x, y] of list) { cv.put(x, y, fill); cells.push([x, y]); }
    };
    arm(-1, p.armL);
    arm(1, p.armR);
    cv.outline(cells, LINE);
    const eyes = small ? [[x0 + 2, y0 + 2], [x0 + 7, y0 + 2]] : [[x0 + 4, y0 + 4], [x0 + 9, y0 + 4]];
    pxEyes(cv, eyes, c, p.eyes, DARK);
    const top = cells.reduce((m, [, y]) => Math.min(m, y), 99) - 1;
    return { mid: x0 + rows[0].length / 2, top, left: x0, right: x0 + rows[0].length - 1, ground: y0 + rows.length + 1, midY: y0 + 4, front: y0 + 4, eyes, hands: true, x0, y0 };
  }

  addLook('codex', 'cloud', 'Cloud', {
    name: 'Codex',
    swatch: 0x8E919C,
    fidgets: ['wave', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        case 'streaming': return { ...b, prop: 'tokens' };
        case 'writing': return { ...b, dy: -1, prop: 'braces' };
        case 'failed': return { ...b, prop: 'bolt' };
        case 'broken': return { ...b, dy: 1, prop: 'rain' };
        case 'running': return { ...b, dy: Math.floor(t * 10) % 2 ? 0 : -1, prop: 'run' };
        case 'offline': return { ...b, prop: 'stall', armL: 'down', armR: 'down' };
        case 'locked': return { ...b, prop: 'padlock', armL: 'down', armR: 'down', look: [0, 1] };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => cloud(g, q, c));
      const a = cloud(cv, q, c);
      const t = q.propT;
      switch (q.prop) {
        case 'think': case 'stall': {
          const k = Math.floor(t * 8) % 6;
          const stall = q.prop === 'stall';
          fitDraw(cv, g => HEX.forEach(([dx, dy], i) => g.put(a.mid - 2 + dx, a.top - 5 + dy,
            stall ? (i === 0 && Math.floor(t * 2) % 2 ? '#D9534F' : '#C5C8D0') : i === k ? GREEN : i === (k + 5) % 6 ? '#7FCDB7' : '#C5C8D0')));
          break;
        }
        case 'tokens':
          for (let i = 0; i < 3; i++) {
            const u = frac(steps(t, 8) * 0.8 + i / 3);
            fitDraw(cv, g => g.rect(a.x0 + 3 + i * 3, a.ground + rnd(u * 2), 2, 1, i === 1 ? GREEN : '#9EA1AB'), null);
          }
          break;
        case 'braces':
          fitDraw(cv, g => {
            const y = a.ground;
            g.glyph(['.#', '#.', '.#'], a.x0 + 1, y, LINE);
            g.glyph(['#.', '.#', '#.'], a.x0 + 11, y, LINE);
            const len = Math.floor(frac(t / 2) * 8);
            g.rect(a.x0 + 3, y + 1, len, 1, DARK);
            if (Math.floor(t * 3) % 2) g.rect(a.x0 + 3 + len, y, 1, 3, GREEN);
          }, null);
          break;
        case 'bolt':
          if (t > 0.3 && Math.floor(t * 6) % 3 !== 0) fitDraw(cv, g => g.glyph(['.##', '.#.', '##.', '.#.', '#..'], a.mid - 1, a.ground - 1, '#F2C230'), null);
          break;
        case 'rain':
          for (let i = 0; i < 4; i++) {
            const u = frac(steps(t, 8) * 1.2 + i / 4);
            fitDraw(cv, g => g.rect(a.x0 + 2 + i * 3, a.ground - 1 + rnd(u * 2), 1, 2, '#7FB4FF'), null);
          }
          break;
        case 'padlock':
          // A padlock held out in front, under the cloud.
          fitDraw(cv, g => g.glyph(['.##.', '#..#', 'GGGG', 'GKKG', 'GGGG'], a.mid - 2, a.ground - 2, '#8D94A6', { G: '#C9A23A', K: '#263349' }), null);
          break;
        case 'sparks':
          pxProp(cv, back, q, a, { ...pal, main: GREEN }, c);
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Codex · Folio (prototype)
//
// Codex's own design (2026-10-16): a little stack of cream paper pages with
// a gold dog-ear, a blue bookmark sticking up, the family's slit eyes, a
// small smile, bracket hands and short feet. Redrawn on the 22-cell grid in
// flat colours with a navy line; the concept's soft shading, extra page
// layers and blush don't survive 44 DIP, so they're left out. (v5.2: a
// look Codex can wear. Offline its page goes blank and the bookmark
// flickers; locked, a binder clip shuts it at the top.)

(() => {
  const PAPER = '#FFF2D8', BACK = '#E6D8BA', INK = '#263349', GOLD = '#F2BB55', CREASE = '#C9922E';
  const BLUE = '#4D96EB', BLUE_DARK = '#3166A4', FOOT = '#D8C9AD';
  const DIM_PAPER = '#D9D2C4', DIM_GOLD = '#B9A57E';
  const X0 = 7, Y0 = 5;
  // 9 × 10 cells; G is the folded corner, which flops lower when it fails.
  const body = rows => [...rows, ...Array.from({ length: 10 - rows.length }, () => 'PPPPPPPPP')];
  const PAGE = body(['PPPPPPG..', 'PPPPPPGG.', 'PPPPPPGGG']);
  const DROOP = body(['PPPPPP...', 'PPPPPPG..', 'PPPPPPGG.', 'PPPPPPGGG']);
  const MINI = ['.b....', 'ppppg.', 'pppppg', 'pppppp', 'pppppp', '.p..p.'];
  const pal = {
    main: BLUE, body: PAPER, dark: INK, light: '#9DBFEA', accent: BLUE, ink: INK, paper: '#FFFFFF',
    mini: (cv, x, y) => cv.glyph(MINI, x, y, PAPER, { p: PAPER, g: GOLD, b: BLUE }),
  };
  // Bracket hands: a '[' on the left, a ']' on the right, placed per pose.
  const HAND = ['##', '#.', '#.', '##'];
  // Raised hands step in toward the page, the room the circle leaves.
  const HANDS = { out: [-5, 3], down: [-5, 4], up: [-4, 1], high: [-2, -1], wave: [-3, 0], tap: [-3, 6] };

  function folio(cv, p, c) {
    const dim = p.dim > 0.5;
    const blank = p.blank > 0.5;
    const paper = dim || blank ? DIM_PAPER : PAPER, gold = dim || blank ? DIM_GOLD : GOLD;
    const x0 = X0 + rnd(p.dx), y0 = Y0 + rnd(p.dy);
    const rows = p.droop > 0.5 ? DROOP : PAGE;
    const cells = [];
    const put = (x, y, color) => { cv.put(x, y, color); cells.push([x, y]); };
    // One page behind, a cell down and to the left.
    for (let j = 1; j <= 10; j++) put(x0 - 1, y0 + j, BACK);
    for (let i = 0; i < 8; i++) put(x0 + i, y0 + 10, BACK);
    rows.forEach((row, j) => [...row].forEach((ch, i) => { if (ch !== '.') put(x0 + i, y0 + j, ch === 'G' ? gold : paper); }));
    // The bookmark, two cells above the top edge; it slumps a cell at rest.
    const bm = rnd(p.mark ?? 0);
    const markOff = blank && Math.floor(c.T * 2) % 2;
    for (let j = -2 + bm; j < 2; j++) { put(x0 + 1, y0 + j, markOff ? '#9EA6B4' : BLUE); put(x0 + 2, y0 + j, markOff ? '#7E8696' : BLUE_DARK); }
    // Feet.
    const legs = { stand: [1, 1], walk1: [0, 1], walk2: [1, 0], jump: [0, 0], tuck: [0, 0], sit: [0, 0] }[p.legs] ?? [1, 1];
    [[x0 + 1, legs[0]], [x0 + 5, legs[1]]].forEach(([fx, on]) => { if (on) { put(fx, y0 + 11, FOOT); put(fx + 1, y0 + 11, FOOT); } });
    cv.outline(cells, INK);
    if (!dim) rows.forEach((row, j) => { const g = row.indexOf('G'); if (g > 0) cv.put(x0 + g, y0 + j, CREASE); });
    // Bracket hands, a cell clear of the page.
    for (const [side, mode] of [[-1, p.armL], [1, p.armR]]) {
      const [hx, hy] = HANDS[mode] ?? HANDS.out;
      // Mirrored about the body with its back page and outline (x0 − 2 to x0 + 9).
      cv.glyph(HAND, side < 0 ? x0 + hx : x0 + 6 - hx, y0 + hy, INK, {}, side > 0);
    }
    const eyes = [[x0 + 2, y0 + 3], [x0 + 6, y0 + 3]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    // A small smile, unless the mood is on the eyes alone.
    const e = c.eye;
    if (p.droop < 0.5 && e.cross < 0.5 && e.sad < 0.5 && p.eyes !== 'wide' && e.sleep < 0.5) {
      cv.put(x0 + 3, y0 + 6, INK); cv.put(x0 + 4, y0 + 7, INK); cv.put(x0 + 5, y0 + 6, INK);
    }
    return {
      x0, y0, eyes, hands: false, mid: x0 + 4, top: y0 - 2, left: x0 - 1, right: x0 + 9, ground: y0 + 12,
      midY: y0 + 5, front: y0 + 5, pageX: x0,
    };
  }

  addLook('codex', 'folio', 'Folio', {
    name: 'Codex',
    swatch: 0x8D94A6,
    fidgets: ['tidy', 'smooth', 'stretch'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, mark: 0, droop: 0, blank: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        // Thinking: a hand to the chin, blue dots counting up beside the head.
        case 'thinking': return { ...b, armL: 'up', armR: 'out', prop: 'dots', look: [1, -1] };
        // Writing: both hands in front, a blue stroke growing between them.
        case 'writing': return { ...b, armL: 'tap', armR: 'tap', dy: 0, prop: 'stroke', look: [0, 1] };
        case 'failed': return { ...b, droop: 1 };
        case 'offline': return { ...b, prop: '', blank: 1 };
        case 'locked': return { ...b, prop: 'clip', armL: Math.floor(t * 2) % 3 === 0 ? 'high' : 'up', look: [0, -1] };
        case 'broken': return { ...b, droop: 1 };
        case 'resting': return { ...b, mark: 1 };
        case 'sleeping': return { ...b, mark: 1 };
        // Fidgets: straighten the bookmark, smooth the dog-ear, a little stretch.
        case 'tidy': return { ...b, armL: t > 0.3 && t < 2.2 ? 'high' : 'out', mark: t > 0.7 && t < 1.5 ? 1 : 0, look: [-1, -1], gazeW: 0 };
        case 'smooth': return { ...b, armR: t > 0.3 && t < 2.4 ? 'high' : 'out', droop: t > 0.8 && t < 1.8 ? 1 : 0, look: [1, -1], gazeW: 0 };
        case 'stretch': {
          const up = t > 0.5 && t < 1.8;
          return { ...b, armL: up ? 'high' : 'out', armR: up ? 'high' : 'out', dy: up ? -1 : 0, legs: up ? 'jump' : 'stand', happy: up ? 1 : 0, gazeW: 0 };
        }
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => folio(g, q, c));
      const a = folio(cv, q, c);
      const t = q.propT;
      switch (q.prop) {
        case 'dots': {
          const n = 1 + Math.floor(t * 2) % 3;
          fitDraw(cv, g => { for (let i = 0; i < n; i++) g.put(a.right + 1 + i, a.top + 2 - i, BLUE); });
          break;
        }
        case 'clip':
          // A binder clip shut on the page's top edge.
          fitDraw(cv, g => g.glyph(['#.#', '#.#', 'sss', 'sss'], a.x0 + 4, a.y0 - 2, '#A9AEBB', { s: '#3F4350' }), null);
          break;
        case 'stroke': {
          const len = 1 + Math.floor(frac(t / 1.2) * 6);
          fitDraw(cv, g => g.rect(a.x0 + 1, a.y0 + 8, len, 1, BLUE), null);
          break;
        }
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Codex · the scroll (original, after Folio)
//
// Inspired by Codex's Folio, redrawn for the 22-cell grid rather than traced:
// the page becomes a little scroll, and its two rolled ends stand in for
// Folio's bracket hands. It lifts a roll to wave, types lines across its
// parchment, curls up a cell for a stretch, and wears gold knobs like
// Folio's dog-ear. (v5.2: a look Codex can wear. Offline it rolls half
// shut and greys, glancing about; locked, a red ribbon ties it.)

(() => {
  const PARCH = '#FFF2D8', PARCH_LOW = '#EADBBE', INK = '#263349', LINE = '#4D96EB';
  const ROLL_LIGHT = '#EFD9AF', ROLL = '#D9B57A', ROLL_DARK = '#C09A5C', KNOB = '#F2BB55';
  const DIM_PARCH = '#D9D2C4', DIM_ROLL = '#B8AE9C';
  const X0 = 5, Y0 = 7;
  const MINI = ['#....#', '#pppp#', '#pppp#', '#....#'];
  const pal = {
    main: LINE, body: PARCH, dark: INK, light: '#9DBFEA', accent: LINE, ink: INK, paper: '#FFFFFF',
    mini: (cv, x, y) => cv.glyph(MINI, x, y, ROLL, { p: PARCH }),
  };
  const LIFT = { up: 1, high: 1, wave: 1, tap: 1 };

  function scroll(cv, p, c) {
    const dim = p.dim > 0.5 || (p.fade > 0.5 && Math.floor(c.T * 1.5) % 2);
    const x0 = X0 + rnd(p.dx), y0 = Y0 + rnd(p.dy);
    const curl = rnd(p.curl ?? 0);
    const cells = [];
    const put = (x, y, color) => { cv.put(x, y, color); cells.push([x, y]); };
    // The parchment, narrower as it curls.
    for (let j = 1; j <= 6; j++) for (let i = 2 + curl; i <= 10 - curl; i++) put(x0 + i, y0 + j, dim ? DIM_PARCH : j === 6 ? PARCH_LOW : PARCH);
    // The two rolls, each lifted a cell when its "arm" is up, with a gold
    // knob on its inner side at both ends.
    for (const [side, mode] of [[-1, p.armL], [1, p.armR]]) {
      const rx = side < 0 ? x0 + curl : x0 + 11 - curl;
      const ry = y0 - (LIFT[mode] && (mode !== 'wave' || Math.floor(c.T * 4) % 2) ? 1 : 0);
      for (let j = 0; j < 8; j++) {
        put(rx, ry + j, dim ? DIM_ROLL : side < 0 ? ROLL_LIGHT : ROLL);
        put(rx + 1, ry + j, dim ? DIM_ROLL : side < 0 ? ROLL : ROLL_DARK);
      }
      const kx = side < 0 ? rx + 1 : rx;
      put(kx, ry - 1, dim ? DIM_ROLL : KNOB);
      put(kx, ry + 8, dim ? DIM_ROLL : KNOB);
    }
    cv.outline(cells, INK);
    const eyes = [[x0 + 4, y0 + 2], [x0 + 8, y0 + 2]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    return { x0, y0, eyes, hands: false, mid: x0 + 6, top: y0 - 1, left: x0, right: x0 + 12, ground: y0 + 9, midY: y0 + 4, front: y0 + 4, pageX: x0 + 2 };
  }

  addLook('codex', 'scroll', 'Scroll', {
    name: 'Codex',
    swatch: 0xA0998A,
    fidgets: ['curl', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, curl: 0, fade: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        // Thinking: the rolls bob in turn, dots counting beside it.
        case 'thinking': return { ...b, armL: Math.floor(t * 2) % 2 ? 'up' : 'out', armR: Math.floor(t * 2) % 2 ? 'out' : 'up', prop: 'dots' };
        // Writing: lines type themselves across the parchment as the rolls tap.
        case 'writing': return { ...b, dy: 0, armL: Math.floor(t * 6) % 2 ? 'tap' : 'out', armR: Math.floor(t * 6) % 2 ? 'out' : 'tap', prop: 'lines', look: [0, 1] };
        case 'reading': return { ...b, prop: 'text', look: [Math.floor(frac(t / 1.6) * 3) - 1, 1] };
        case 'finished': return { ...b, armL: t < 1.2 ? 'up' : 'out', armR: t < 1.2 ? 'up' : 'out' };
        case 'failed': return { ...b, prop: 'blot' };
        case 'broken': return { ...b, curl: 1 };
        case 'offline': return { ...b, prop: '', curl: 1, fade: 1 };
        case 'locked': return { ...b, prop: 'ribbon', armL: 'out', armR: Math.floor(t * 2) % 3 === 0 ? 'up' : 'out', look: [0, 1] };
        case 'resting': return { ...b, curl: 1 };
        // Resting: rolled in a turn, eyes closed and content.
        case 'sleeping': return { ...b, curl: 1 };
        // Fidget: it curls up a cell, holds, and unrolls.
        case 'curl': return { ...b, curl: t > 0.4 && t < 2.0 ? 1 : 0, happy: t > 0.4 && t < 2.0 ? 1 : 0, gazeW: 0 };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => scroll(g, q, c));
      const a = scroll(cv, q, c);
      const t = q.propT;
      switch (q.prop) {
        case 'dots': {
          const n = 1 + Math.floor(t * 2) % 3;
          fitDraw(cv, g => { for (let i = 0; i < n; i++) g.put(a.mid - 1 + i * 2, a.top - 2, LINE); });
          break;
        }
        case 'lines': {
          const len = rnd(frac(t / 2) * 12);
          for (let i = 0; i < Math.min(len, 7); i++) cv.put(a.x0 + 3 + i, a.y0 + 4, LINE);
          for (let i = 0; i < Math.max(0, len - 7); i++) cv.put(a.x0 + 3 + i, a.y0 + 5, INK);
          break;
        }
        case 'text':
          for (const [w, y] of [[6, 4], [4, 5]]) for (let i = 0; i < w; i++) cv.put(a.x0 + 3 + i, a.y0 + y, '#8D94A6');
          break;
        case 'ribbon': {
          // A red ribbon round its middle, tied in a bow.
          const R = '#D9534F', RD = '#A8322F', y = a.y0 + 5;
          for (let x = a.x0 + 2; x <= a.x0 + 10; x++) cv.put(x, y, R);
          for (const [dx, dy, col] of [[-1, -1, R], [1, -1, R], [0, 0, RD], [-1, 1, R], [1, 1, R]]) cv.put(a.mid + dx, y + dy, col);
          break;
        }
        case 'blot': {
          const r = Math.min(3, Math.floor(t * 6));
          for (const [dx, dy] of [[0, 0], [1, 0], [0, 1], [-1, 0], [1, 1], [2, 0], [-1, 1], [0, -1]].slice(0, 2 + r * 2)) cv.put(a.mid + dx, a.y0 + 4 + dy, '#3A2F4A');
          break;
        }
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Antigravity · the pixel rainbow
//
// Antigravity's "A" as a pixel arch in rainbow stripes, hovering a couple
// of pixels above its own shadow. Weightless and bright: its stripes flow
// while it thinks, it throws confetti as it talks, sends rainbow Wi-Fi arcs
// up for the web, launches a little rocket to send, and when something
// fails the antigravity gives out and it drops to the ground, faded.
// (v5.2: a look Antigravity can wear again. Offline its stripes grey out
// one by one and its hover stutters; locked, a padlock hangs from its
// crossbar.)

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
    // Offline: the stripes go grey from the top, one at a time, then return.
    const lost = p.lost > 0.5 ? Math.floor(c.T * 2) % 8 : -1;
    rows.forEach((row, j) => [...row].forEach((ch, i) => {
      if (ch !== '#') return;
      // Lift a foot off the ground while walking.
      const foot = j >= rows.length - 2;
      if (foot && j === rows.length - 1 && ((p.legs === 'walk1' && i < 6) || (p.legs === 'walk2' && i >= 6))) return;
      const band = ((Math.floor(j * 6 / rows.length) + flow) % 6 + 6) % 6;
      const out = lost >= 0 && Math.floor(j * 6 / rows.length) < lost;
      cv.put(x0 + i, y0 + j, gray > 0.5 || out ? mixHex(BANDS[band], GRAY, 0.75) : BANDS[band]);
    }));
    // Its shadow: narrower and fainter the higher it floats.
    const w = clamp(10 - lift * 2, 4, 10);
    shadowCv.rect(x0 + 6 - w / 2, GROUND, w, 1, SHADOW);
    const eyeRow = y0 + Math.round(rows.length * 0.42);
    const eyes = [[x0 + 3, eyeRow], [x0 + 8, eyeRow]];
    pxEyes(cv, eyes, c, p.eyes, DARK);
    return { mid: x0 + 6, top: y0, left: x0, right: x0 + 12, ground: GROUND, midY: y0 + 5, front: y0 + 6, eyes, x0, y0 };
  }

  addLook('antigravity', 'rainbow', 'Rainbow A', {
    name: 'Antigravity',
    swatch: 0x2FA3D6,
    // v5: 'shimmer' flowed its stripes like thinking; floating up to look
    // down at its own shadow is idle play.
    fidgets: ['hop', 'float', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, lift: 2, flow: 0, lost: 0 },
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
        case 'resting': return { ...b, lift: 0, dy: 0 };
        case 'offline': return { ...b, prop: '', lost: 1, lift: hash(Math.floor(T * 3)) > 0.75 ? 1 : 2, dy: 0 };
        case 'locked': return { ...b, prop: 'padlock', lift: 2, dy: 0, look: [0, 1] };
        case 'float': {
          // Up three cells, a look down at its shrunken shadow, and back.
          const lift = t < 0.6 ? stepsTo(t / 0.6, 2, 5) : t < 2.2 ? 5 : t < 2.8 ? stepsTo((t - 2.2) / 0.6, 5, 2) : hover;
          return { ...b, lift, dy: 0, look: t < 0.6 ? [0, -1] : t < 2.2 ? [0, 1] : [0, 0], gazeW: 0 };
        }
        case 'hop': return { ...b, lift: 2 - rnd(b.dy), dy: 0 };
        // Resting: a low hover, one cell up for each slow breath.
        case 'sleeping': return { ...b, lift: 3 - b.dy, dy: 0 };
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
        case 'padlock':
          // A steel padlock hanging from the crossbar, swinging a cell.
          fitDraw(cv, g => {
            const x = a.x0 + 4 + (Math.floor(t * 1.5) % 2), y = a.y0 + 8;
            g.glyph(['#..#', '#..#', 'SSSS', 'SKKS', 'SSSS'], x, y, '#8D94A6', { S: '#5E616C', K: '#1E2033' });
          }, null);
          break;
        case 'sparks':
          pxProp(cv, back, q, a, { ...pal, main: BANDS[0] }, c);
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(shade, { ...c, rim: 0 }, 0.22) + pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Antigravity · Aeri (prototype)
//
// Codex's own design (2026-10-16): a floating blue "A" with a cream face
// band, little wing hands, and four satellite blocks (blue, green, yellow,
// coral, one of each) that arrange themselves to show what it's doing.
// Redrawn on the 22-cell grid in flat colours, hovering over its shadow like
// the v4 "A". Like that "A" it has the white rim and no dark line: at 44 DIP
// the line closed the letter's counter and left no room for the satellites.
// The concept's blush and soft shading are left out. (v5.2: a look
// Antigravity can wear. Its coral mouth shows only when it's happy, the
// user's rule for the balloon; offline its satellites drift out and blink;
// locked they click into a padlock between its legs.)

(() => {
  const BLUE = '#4D96EB', SHADE = '#3166A4', HIGH = '#B7D5F5', FACE = '#FFF2D8', INK = '#263349';
  const SAT = ['#4D96EB', '#56AB78', '#F2BB55', '#E77C65'];
  const DIM_BLUE = '#93A6BF', DIM_SHADE = '#7E8DA2', SHADOW = '#2A2E3A';
  // Its shadow is on row 18; it never lands below lift 1, so its feet's rim
  // stays inside the drawer's circle.
  const GROUND = 18, X0 = 4;
  // 13 × 13 cells, bold like Codex's drawing: F is the face band across the
  // crossbar; the counter above it stays open.
  const A = [
    '.....###.....',
    '....#####....',
    '....##.##....',
    '...###.###...',
    '...##...##...',
    '..##FFFFF##..',
    '..#FFFFFFF#..',
    '.##FFFFFFF##.',
    '.###.....###.',
    '.###.....###.',
    '###.......###',
    '###.......###',
    '##.........##',
  ];
  const MINI = ['..#..', '.#.#.', '.###.', '#...#'];
  const pal = {
    main: BLUE, body: BLUE, dark: INK, light: HIGH, accent: SAT[3],
    mini: (cv, x, y) => cv.glyph(MINI, x, y, BLUE),
  };
  // Wing hands, 2 × 2, beside the face band, per pose.
  const WINGS = { out: [0, 5], up: [0, 3], high: [1, 1], wave: [1, 2], down: [0, 7], tap: [1, 8] };
  // Where the four satellites sit (top-left cells), per layout.
  const LAYOUT = {
    idle: [[2, 11], [4, 6], [16, 6], [18, 11]],
    ground: [[7, 16], [9, 17], [11, 17], [13, 16]],
    low: [[2, 12], [4, 7], [16, 7], [18, 12]],
  };

  function body(cv, shadowCv, p, c) {
    const lift = Math.max(1, rnd(p.lift));
    const x0 = X0 + rnd(p.dx);
    const y0 = GROUND - 1 - lift + rnd(p.dy) - (A.length - 1);
    const dim = p.dim > 0.5;
    A.forEach((row, j) => [...row].forEach((ch, i) => {
      if (ch === '.') return;
      const right = i > 6 || (i === 6 && j > 0);
      cv.put(x0 + i, y0 + j, ch === 'F' ? FACE : dim ? (right ? DIM_SHADE : DIM_BLUE) : right ? SHADE : BLUE);
    }));
    if (!dim) { cv.put(x0 + 5, y0, HIGH); cv.put(x0 + 4, y0 + 2, HIGH); cv.put(x0 + 3, y0 + 4, HIGH); }
    for (const [side, mode] of [[-1, p.armL], [1, p.armR]]) {
      const [wx, wy] = WINGS[mode] ?? WINGS.out;
      // Mirrored about the letter (x0 to x0 + 10).
      const x = side < 0 ? x0 + wx : x0 + 11 - wx;
      cv.rect(x, y0 + wy, 2, 2, dim ? DIM_BLUE : side < 0 ? BLUE : SHADE);
    }
    // Its shadow: narrower the higher it floats.
    const w = clamp(12 - lift * 2, 4, 10);
    shadowCv.rect(x0 + 7 - w / 2, GROUND, w, 1, SHADOW);
    const eyes = [[x0 + 4, y0 + 5], [x0 + 8, y0 + 5]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    // A small coral mouth, as Codex drew it.
    if (c.eye.happy > 0.5) cv.put(x0 + 6, y0 + 7, '#E77C65');
    return { mid: x0 + 6, top: y0, left: x0, right: x0 + 12, ground: GROUND, midY: y0 + 6, front: y0 + 7, eyes, x0, y0 };
  }

  // The satellites: four blocks, one of each colour, never a fifth.
  function satellites(cv, p, c, a) {
    const T = c.T, t = p.propT;
    const base = (LAYOUT[p.sats] ?? LAYOUT.idle).map(([x, y]) => [x, y]);
    const off = [0, 0, 0, 0];
    switch (p.sats) {
      case 'idle': case 'low':
        // A slow bob, each on its own beat.
        for (let i = 0; i < 4; i++) off[i] = frac(T / (p.sats === 'low' ? 4 : 2.4) + i / 4) < 0.5 ? 0 : -1;
        break;
      case 'arc': {
        // Thinking: the blocks light up one at a time, round the head.
        off[Math.floor(t * 4) % 4] = -1;
        break;
      }
      case 'cheer':
        for (let i = 0; i < 4; i++) off[i] = t < 1.4 && Math.floor(t * 6) % 2 ? -1 : 0;
        break;
      case 'stack': {
        // Building: they file over to the right and stack into a tower.
        const n = Math.floor(t * 2.5) % 6;
        const tower = [[17, 13], [17, 11], [17, 9], [17, 7]].map(([x, y]) => [x + 1, y - 1]);
        for (let i = 0; i < 4; i++) if (i < n) base[i] = tower[i];
        break;
      }
      case 'roll': {
        const k = Math.floor(t * 2.5);
        if (k < 4) off[k] = -2;
        break;
      }
      case 'swap':
        if (t > 0.6 && t < 2.2) { [base[0], base[3]] = [base[3], base[0]]; [base[1], base[2]] = [base[2], base[1]]; }
        break;
      case 'lost':
        // Offline: out a cell each, blinking dim in turn.
        base.forEach((b, i) => { b[0] += i < 2 ? -1 : 1; b[1] += 1; });
        break;
      case 'lock': {
        // Locked: a 2 × 2 of blocks between its legs, a steel shackle on top.
        const x = a.x0 + 4, y = a.y0 + 9;
        [[x, y], [x + 2, y], [x, y + 2], [x + 2, y + 2]].forEach((q, i) => { base[i] = q; });
        cv.glyph(['.##.', '#..#'], x, y - 2, '#8D94A6');
        break;
      }
      case 'drift':
        // One drifts off: coral slides out and down.
        base[3] = [18, 11 + Math.min(3, Math.floor(t * 3))];
        break;
    }
    for (let i = 0; i < 4; i++) {
      const x = base[i][0], y = base[i][1] + off[i] + (p.satDrop ?? 0);
      const dull = p.dim > 0.5 || (p.sats === 'lost' && (i + Math.floor(c.T * 2)) % 2);
      fitDraw(cv, g => g.rect(x, y, 2, 2, dull ? mixHex(SAT[i], '#9EA3AE', 0.6) : SAT[i]), null);
    }
  }

  addLook('antigravity', 'aeri', 'Aeri', {
    name: 'Antigravity',
    swatch: 0x4D96EB,
    fidgets: ['roll', 'swap', 'balance'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, lift: 2, sats: 'idle', satDrop: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      const hover = frac(T / 1.6) < 0.5 ? 2 : 3;
      switch (s) {
        case 'thinking': return { ...b, lift: 2, sats: 'arc', prop: '', armL: 'up', look: [0, -1] };
        case 'writing': case 'working': case 'running': return { ...b, lift: hover, dy: 0, sats: 'stack', prop: '', armR: 'out', look: [1, 0] };
        case 'finished': return { ...b, lift: 2, dy: 0, sats: 'cheer' };
        case 'failed': return { ...b, lift: t < 0.3 ? 2 : 1, sats: 'drift' };
        case 'broken': return { ...b, lift: 1, dy: 0, sats: 'ground' };
        case 'resting': return { ...b, lift: 1, dy: 0, sats: 'ground' };
        case 'sleeping': return { ...b, lift: 3 - b.dy, dy: 0, sats: 'low' };
        case 'asking': return { ...b, lift: 2 - rnd(b.dy), dy: 0 };
        case 'offline': return { ...b, prop: '', lift: 2, dy: 0, sats: 'lost' };
        case 'locked': return { ...b, prop: '', lift: 2, dy: 0, sats: 'lock', armL: 'down', armR: Math.floor(t * 2) % 3 === 0 ? 'up' : 'down', look: [0, 1] };
        case 'searching': return { ...b, lift: hover };
        // Fidgets: a roll call, a swap and a balancing act.
        case 'roll': return { ...b, lift: hover, dy: 0, sats: 'roll', look: [1, -1], gazeW: 0 };
        case 'swap': return { ...b, lift: hover, dy: 0, sats: 'swap', happy: t > 0.6 && t < 2.2 ? 1 : 0, gazeW: 0 };
        case 'balance': return { ...b, lift: t > 0.4 && t < 2.2 ? 3 : 2, dy: 0, satDrop: t > 0.4 && t < 2.2 ? 1 : 0, armL: 'up', armR: 'up', gazeW: 0 };
        default: return { ...b, lift: hover, dy: 0 };
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas(), shade = canvas();
      const q = settle(p, (g, q) => body(g, canvas(), q, c));
      const a = body(cv, shade, q, c);
      satellites(cv, q, c, a);
      if (q.prop === 'sparks') pxProp(cv, back, q, a, { ...pal, main: SAT[2] }, c);
      else pxProp(cv, back, q, a, pal, c);
      return pixels(shade, { ...c, rim: 0 }, 0.22) + pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Antigravity · the floating blocks (original, after Aeri)
//
// Inspired by Codex's Aeri, drawn fresh for the 22-cell grid rather than
// traced: Aeri's four satellites (blue, coral, green, yellow) become the body
// itself, four toy blocks that float as an "A" with air between them. The
// blue block wears the face, the coral and green ones are its slanted legs,
// the yellow plank its crossbar. They bob on their own beats; they drift
// apart while it thinks, the plank slides in and out while it builds, they
// click together when it's done or at rest, and fall in a heap when it fails.
// (v5.2: a look Antigravity can wear. Offline the blocks drift apart and
// blink grey in turn; locked they click together under a steel strap.)

(() => {
  const INK = '#263349', SHADOW = '#2A2E3A', FACE = '#FFF2D8', DIM_FACE = '#D9D2C4';
  // [fill, highlight, shade] per block.
  const BLUE = ['#4D96EB', '#B7D5F5', '#3166A4'], CORAL = ['#E77C65', '#F6B4A6', '#C55E4A'];
  const GREEN = ['#56AB78', '#A9DDBB', '#3F8A5D'], YELLOW = ['#F2BB55', '#FBE2AE', '#C9922E'];
  const DIM = ['#9EA6B4', '#C7CCD6', '#7E8696'];
  const GROUND = 17;
  // Each block: its cells as [x, y] from its own top-left, where it floats,
  // where it lands when the blocks click together, and where it lies in the
  // heap. The legs lean out, one cell every two rows, like an "A".
  const box = (w, h) => Array.from({ length: w * h }, (_, i) => [i % w, Math.floor(i / w)]);
  const lean = dir => [0, 1, 2, 3, 4, 5].flatMap(j => [0, 1, 2].map(i => [i + dir * Math.floor(j / 2), j]));
  // In the heap the legs drop to the ground, tip over on their sides, and
  // the head lands across them, over the plank.
  const BLOCKS = [
    { cells: box(6, 5), col: BLUE, at: [8, 3], snap: [8, 4], heap: [8, 10] },
    { cells: lean(-1), lying: box(5, 2), col: CORAL, at: [6, 9], snap: [7, 9], drop: [6, 11], heap: [4, 15] },
    { cells: lean(1), lying: box(5, 2), col: GREEN, at: [13, 9], snap: [12, 9], drop: [13, 11], heap: [13, 15] },
    { cells: box(4, 2), col: YELLOW, at: [9, 11], snap: [9, 11], heap: [9, 15] },
  ];
  const MINI = ['.##.', '.##.', '#..#', '#..#'];
  const pal = {
    main: BLUE[0], body: BLUE[0], dark: INK, light: BLUE[1], accent: CORAL[0],
    mini: (cv, x, y) => { cv.rect(x + 1, y, 2, 2, BLUE[0]); cv.put(x, y + 2, CORAL[0]); cv.put(x, y + 3, CORAL[0]); cv.put(x + 3, y + 2, GREEN[0]); cv.put(x + 3, y + 3, GREEN[0]); },
  };

  function blocks(cv, shadowCv, p, c) {
    const dim = p.dim > 0.5;
    const T = c.T;
    const snap = clamp(p.snap ?? 0), fall = clamp(p.fall ?? 0), spread = rnd(p.spread ?? 0);
    const placed = BLOCKS.map((b, i) => {
      // Where it is: floating, clicked together, or fallen, in whole cells.
      let x = lerp(b.at[0], b.snap[0], snap), y = lerp(b.at[1], b.snap[1], snap);
      // A leg drops standing, then tips over at the bottom.
      const tipped = b.lying && fall >= 0.5;
      const to = b.drop && !tipped ? b.drop : b.heap, f = b.drop && !tipped ? fall * 2 : fall;
      x = rnd(tipped ? b.heap[0] : lerp(x, to[0], f));
      y = rnd(tipped ? b.heap[1] : lerp(y, to[1], f));
      if (fall < 1) {
        x += rnd(p.dx); y += rnd(p.dy);
        // Antigravity: each block bobs on its own beat while they float.
        if (p.bob && snap < 0.5) y -= frac(T / 2.4 + i * 0.27) < 0.5 ? 0 : 1;
        // Thinking: they drift apart, a cell each way.
        x += [0, -spread, spread, 0][i]; y += [-spread, 0, 0, spread][i];
        // Walking: the legs take turns stepping up.
        if ((i === 1 && (p.legs === 'walk1' || p.legs === 'jump')) || (i === 2 && (p.legs === 'walk2' || p.legs === 'jump'))) y -= 1;
        // The plank sliding in and out while it builds.
        if (i === 3) x += rnd(p.bar ?? 0);
      }
      const lost = p.lost > 0.5 && (i + Math.floor(T * 2)) % 2;
      return { ...b, cells: tipped ? b.lying : b.cells, x, y, colors: dim || lost ? DIM : b.col };
    });
    // Its fidget: the two legs trade places, colours and all.
    if (p.swap) [placed[1].colors, placed[2].colors] = [placed[2].colors, placed[1].colors];
    for (const b of placed) {
      const top = Math.min(...b.cells.map(([, y]) => y)), bottom = Math.max(...b.cells.map(([, y]) => y));
      const left = Math.min(...b.cells.filter(([, y]) => y === top).map(([x]) => x));
      for (const [i, j] of b.cells) cv.put(b.x + i, b.y + j, j === bottom ? b.colors[2] : j === top && i === left ? b.colors[1] : b.colors[0]);
    }
    // The face band across the head, as on Aeri's "A".
    cv.rect(placed[0].x, placed[0].y + 1, 6, 2, dim ? DIM_FACE : FACE);
    const head = placed[0];
    // Its shadow: narrower the higher the lowest block floats.
    const low = Math.max(...placed.map(b => b.y + Math.max(...b.cells.map(([, y]) => y))));
    const w = clamp(12 - 2 * (GROUND - 1 - low), 6, 12);
    shadowCv.rect(11 - w / 2, GROUND, w, 1, SHADOW);
    const eyes = [[head.x + 1, head.y + 1], [head.x + 4, head.y + 1]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    return { mid: head.x + 3, top: head.y, left: 4, right: 17, ground: GROUND, midY: head.y + 5, front: head.y + 5, eyes, x0: head.x, y0: head.y };
  }

  addLook('antigravity', 'blocks', 'Blocks', {
    name: 'Antigravity',
    swatch: 0x3166A4,
    fidgets: ['shuffle', 'click', 'hop'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, bob: 1, spread: 0, snap: 0, bar: 0, fall: 0, swap: 0, lost: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        // Thinking: the blocks drift apart and back, eyes up.
        case 'thinking': return { ...b, spread: frac(t / 2.4) < 0.5 ? 1 : 0, look: [0, -1], prop: '' };
        // Building: the plank slides in and out of the "A".
        case 'writing': case 'working': return { ...b, dy: 0, bar: [0, -1, 0, 1][Math.floor(t * 4) % 4], look: [0, 1], prop: '' };
        case 'finished': return { ...b, snap: t < 1.4 ? 1 : 0 };
        case 'failed': return { ...b, bob: 0, fall: clamp((t - 0.2) / 0.5), prop: '' };
        case 'broken': return { ...b, bob: 0, fall: 1, dy: 0 };
        case 'offline': return { ...b, prop: '', spread: 1, lost: 1, bob: 0 };
        case 'locked': return { ...b, prop: 'strap', bob: 0, snap: 1, dy: 0, look: [0, 1] };
        case 'resting': return { ...b, bob: 0, fall: 1, dy: 0 };
        // Resting: clicked together into one quiet "A", a cell lower.
        case 'sleeping': return { ...b, bob: 0, snap: 1, dy: b.dy - 1 };
        // Fidgets: the legs trade places; the blocks click together and apart.
        case 'shuffle': return { ...b, swap: t > 0.5 && t < 2.2 ? 1 : 0, happy: t > 0.5 && t < 2.2 ? 1 : 0, gazeW: 0 };
        case 'click': return { ...b, bar: 0, dy: 0, snap: t > 0.4 && t < 1.8 ? (Math.floor(t * 3) % 2 ? 1 : 0) : 0, look: [0, 1], gazeW: 0 };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas(), shade = canvas();
      const q = settle(p, (g, q) => blocks(g, canvas(), q, c));
      const a = blocks(cv, shade, q, c);
      if (q.prop === 'strap') {
        // A steel strap round the clicked-together blocks, a padlock on it.
        cv.rect(6, 12, 10, 1, '#8D94A6');
        cv.glyph(['.#.', '###', '#k#'], 10, 12, '#5E616C', { k: '#1E2033' });
        cv.put(10, 11, '#8D94A6'); cv.put(12, 11, '#8D94A6');
      } else pxProp(cv, back, q, a, pal, c);
      return pixels(shade, { ...c, rim: 0 }, 0.22) + pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Antigravity · the kite (original, after Aeri)
//
// Inspired by Codex's Aeri, drawn fresh for the 22-cell grid rather than
// traced: Aeri floats, so this one flies. A diamond kite in Aeri's blue whose
// cream spar turns its top into an "A", with a face under the spar and
// Aeri's coral mouth. Its three tail bows carry the other three satellite
// colours. The tail sways in the breeze, whips while it works, and lies on
// the ground once the wind drops (resting, broken, asleep). (v5.2: a look
// Antigravity can wear. Its mouth shows only when it's happy; offline its
// string has snapped and it drifts, greying; locked, its tail is staked to
// the ground.)

(() => {
  const LIGHT = '#7DB6F5', BLUE = '#4D96EB', SPAR = '#FFF2D8', INK = '#263349', MOUTH = '#E77C65';
  const DIM_LIGHT = '#B9C1CE', DIM_BLUE = '#9EA6B4', DIM_SPAR = '#D9D2C4', DIM_BOW = '#B8AE9C';
  const BOWS = ['#E77C65', '#F2BB55', '#56AB78'];
  // Half-widths of the sail's rows: a shallow top, the spar at the widest
  // row, and a long point down to the tail.
  const SAIL = [0, 2, 4, 6, 5, 4, 3, 2, 1];
  const SPAR_ROW = 3, C = 10, Y0 = 4, GROUND = 18;
  const MINI = ['..#..', '.###.', '#####', '.###.', '..#..'];
  const pal = {
    main: BLUE, body: BLUE, dark: INK, light: LIGHT, accent: MOUTH, ink: INK,
    mini: (cv, x, y) => { cv.glyph(MINI, x, y, BLUE); cv.rect(x + 1, y + 2, 3, 1, SPAR); },
  };

  function kite(cv, p, c) {
    const dim = p.dim > 0.5 || (p.adrift > 0.5 && Math.floor(c.T * 1.5) % 2);
    const cx = C + rnd(p.dx), y0 = Y0 + rnd(p.dy);
    const cells = [];
    const put = (x, y, color) => { cv.put(x, y, color); cells.push([x, y]); };
    SAIL.forEach((h, j) => {
      for (let i = -h; i <= h; i++) put(cx + i, y0 + j, dim ? (i <= 0 ? DIM_LIGHT : DIM_BLUE) : i <= 0 ? LIGHT : BLUE);
    });
    cv.outline(cells, INK);
    // The spar, poking out a cell each side: with the top, an "A".
    for (let i = -7; i <= 7; i++) cv.put(cx + i, y0 + SPAR_ROW, dim ? DIM_SPAR : SPAR);
    // The tail, from the point of the sail: swaying, lying along the ground
    // past it, with a bow every other cell.
    const amp = p.tail ?? 1, hz = p.tailHz ?? 0.42;
    for (let j = 0; j < 7; j++) {
      const sway = rnd(amp * Math.min(1, (j + 1) / 3) * Math.sin(TAU * c.T * hz - j * 0.9));
      let tx = cx + sway, ty = y0 + SAIL.length + j;
      const lying = ty > GROUND;
      if (lying) { tx = cx + (ty - GROUND); ty = GROUND; }
      const bow = j % 2 === 1 ? (dim ? DIM_BOW : BOWS[(j - 1) / 2]) : null;
      // A bow on the ground is just its knot.
      if (bow && !lying) { cv.put(tx - 1, ty, bow); cv.put(tx, ty, bow); cv.put(tx + 1, ty, bow); } else cv.put(tx, ty, bow ?? INK);
    }
    const eyes = [[cx - 2, y0 + 4], [cx + 2, y0 + 4]];
    pxEyes(cv, eyes, c, p.eyes, INK);
    if (c.eye.happy > 0.5) cv.put(cx, y0 + 6, dim ? DIM_BLUE : MOUTH);
    return { eyes, mid: cx, top: y0, left: cx - 6, right: cx + 6, ground: GROUND, midY: y0 + 4, front: y0 + 6, pageX: cx - 4 };
  }

  addLook('antigravity', 'kite', 'Kite', {
    name: 'Antigravity',
    swatch: 0x5AA0F0,
    fidgets: ['flick', 'dive', 'hop'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, tail: 1, tailHz: 0.42, adrift: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      // Up and down on the breeze, a cell at a time.
      const ride = frac(T / 3) < 0.5 ? 0 : -1;
      switch (s) {
        case 'idle': return { ...b, dy: ride };
        case 'thinking': return { ...b, dy: ride, tail: 2, tailHz: 0.25, prop: 'dots' };
        // Busy: it tacks back and forth, tail whipping.
        case 'writing': case 'working': case 'running': return { ...b, dy: 0, dx: Math.floor(t * 3) % 2, tail: 2, tailHz: 1.6 };
        case 'failed': return { ...b, dy: clamp((t - 0.2) / 0.4) * 3, tail: 0 };
        case 'broken': return { ...b, dy: 3, tail: 0 };
        case 'offline': return { ...b, prop: '', adrift: 1, dy: 1, dx: [0, -1, 0, 1][Math.floor(t / 0.75) % 4], tail: 0.6, tailHz: 0.2 };
        case 'locked': return { ...b, prop: 'stake', dy: 1, tail: 0, look: [0, 1] };
        case 'resting': return { ...b, dy: 3, tail: 0 };
        // Resting: the breeze has dropped. It sinks, its tail lying on the
        // ground, and rises a cell on each slow breath.
        case 'sleeping': return { ...b, dy: b.dy, tail: 0.4, tailHz: 0.12 };
        // Fidgets: a flick of the tail; a little dive and back up.
        case 'flick': return { ...b, dy: ride, tail: t > 0.4 && t < 1.6 ? 2 : 1, tailHz: t > 0.4 && t < 1.6 ? 1.4 : 0.42, happy: t > 0.4 && t < 1.6 ? 1 : 0, gazeW: 0 };
        case 'dive': {
          const d = t < 0.4 ? 0 : t < 0.7 ? 1 : t < 1.0 ? 2 : t < 1.6 ? 1 : 0;
          return { ...b, dy: d, dx: t > 0.4 && t < 1.6 ? 1 : 0, tail: 2, tailHz: 0.9, eyes: 'wide', gazeW: 0 };
        }
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => kite(g, q, c));
      const a = kite(cv, q, c);
      if (q.prop === 'stake') {
        // A wooden stake at the end of its tail, the tail tied fast to it.
        const x = a.mid + 3;
        cv.rect(x, GROUND - 2, 1, 3, '#7E5530');
        cv.put(x, GROUND - 3, '#5E616C');
        cv.put(x - 1, GROUND - 1, INK);
      } else if (q.prop === 'dots') {
        const n = 1 + Math.floor(q.propT * 2) % 3;
        fitDraw(cv, g => { for (let i = 0; i < n; i++) g.put(a.right + i, a.top - i, BLUE); });
      } else pxProp(cv, back, q, a, pal, c);
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Grok · the ring bot (round 3, refitted)
//
// Grok's slashed ring as a little robot: a black pixel ring round a pale
// face, the slash turned into a diagonal antenna, two tiny legs. Cheeky and
// cosmic: the antenna picks up signals to think and sparks as it talks; the
// ring becomes a magnifier to search, wears a planet's ring for the web,
// rides a rocket to run and collapses into a black hole to compact.
// (v5.2: round 3's Grok, a look Grok can wear again, refitted into the
// drawer's circle: a cell smaller ring, the antenna bent in at the top, one
// cell legs' room under it. Offline its antenna loses the signal, a red ×
// blinking by the knob; locked it folds the antenna away and wears a
// shackle over the ring, a padlock like today's ring.)

(() => {
  const INK = '#0B0B0C';
  const FACE = '#EFEFEC';
  const DIM_INK = '#5A5A60';
  const DIM_FACE = '#CFCFCB';
  const CX = 10, CY = 12;
  const MINI = ['.....##', '.###.#.', '#fff#..', '#efe#..', '#fff#..', '.###...'];
  const pal = {
    main: INK, body: INK, dark: INK, light: '#9A9AA0', accent: INK,
    mini: (cv, x, y) => cv.glyph(MINI, x, y, INK, { f: FACE, e: INK }),
  };

  function ringBot(cv, back, p, c) {
    const cx = CX + rnd(p.dx), cy = CY + rnd(p.dy) - rnd(p.lift);
    const dim = p.dim > 0.5;
    const ink = dim ? DIM_INK : INK, face = dim ? DIM_FACE : FACE;
    const inner = p.sq > 0.5 ? 3.0 : 4.1;
    const tick = Math.floor(c.T * 6);
    cv.shape((x, y) => {
      const d = Math.hypot(x - cx - 0.5, y - cy - 0.5);
      return d <= 5.5;
    }, cx - 6, cy - 6, 13, 13, (x, y) => {
      const d = Math.hypot(x + 0.5 - cx - 0.5, y + 0.5 - cy - 0.5);
      if (d < inner) return face;
      if (p.dashed && (Math.floor(Math.atan2(y - cy, x - cx) / (TAU / 16)) + tick) % 2) return face;
      return ink;
    });
    // Legs and arm nubs.
    const legs = { stand: [2, 2], walk1: [1, 2], walk2: [2, 1], jump: [1, 1], tuck: [0, 0], sit: [1, 1] }[p.legs] ?? [2, 2];
    if (!p.rocket) [[cx - 2, legs[0]], [cx + 2, legs[1]]].forEach(([x, h]) => h && cv.rect(x, cy + 6, 1, h, ink));
    const arm = (side, mode) => {
      const x = side < 0 ? cx - 7 : cx + 7;
      const dy = { out: 0, up: -1, high: -2, down: 1, wave: -2, tap: 1 }[mode] ?? 0;
      cv.put(x, cy + dy, ink);
      if (mode === 'high' || mode === 'wave') cv.put(x + (mode === 'wave' ? side : 0), cy + dy - 1, ink);
    };
    arm(-1, p.armL);
    arm(1, p.armR);
    // The antenna: up and jaunty, flopped over, or folded away.
    const knob = [];
    if (p.antenna !== 'off') {
      const droop = p.antenna === 'droop';
      // Steep, like an antenna, so the head never reads as a ♂ sign.
      const stalk = droop ? [[cx + 5, cy - 5], [cx + 6, cy - 5], [cx + 7, cy - 4]] : [[cx + 3, cy - 6], [cx + 4, cy - 7]];
      for (const [x, y] of stalk) cv.put(x, y, ink);
      const [kx, ky] = droop ? [cx + 8, cy - 4] : [cx + 4, cy - 9];
      const glow = p.glow && Math.floor(c.T * 4) % 2;
      cv.rect(kx, ky, 2, 2, glow ? '#F5BE2E' : ink);
      knob.push(kx, ky);
    }
    const eyes = [[cx - 2, cy - 1], [cx + 2, cy - 1]];
    pxEyes(cv, eyes, c, p.eyes, ink);
    return { mid: cx, top: cy - 6, left: cx - 7, right: cx + 7, ground: cy + 8, midY: cy, front: cy + 1, eyes, knob, cx, cy, hands: false };
  }

  addLook('grok', 'ringbot', 'Ring bot', {
    name: 'Grok',
    swatch: 0x2A2A30,
    fidgets: ['wink', 'hop', 'peek'],
    cycle: PIXEL_CYCLE,
    rest: { ...PIXEL_REST, antenna: 'up', glow: 0, lift: 0, rocket: 0, dashed: 0 },
    pose(s, t, T) {
      const b = basePose(s, t, T);
      switch (s) {
        case 'thinking': return { ...b, glow: 1, prop: 'signal' };
        case 'reading': return { ...b, prop: 'scan' };
        case 'streaming': return { ...b, prop: 'zap' };
        case 'searching': return { ...b, antenna: 'off', eyes: 'wide', prop: 'lens' };
        case 'web': return { ...b, dy: 0, prop: 'planet' };
        case 'running': return { ...b, rocket: 1, lift: 2 + (Math.floor(t * 10) % 2), dy: 0, prop: 'flame' };
        case 'compacting': return { ...b, prop: 'swirl' };
        case 'failed': return { ...b, antenna: t > 0.3 ? 'droop' : 'up' };
        case 'broken': return { ...b, antenna: 'droop' };
        case 'resting': return { ...b, antenna: 'droop' };
        case 'sleeping': return { ...b, antenna: 'droop' };
        case 'offline': return { ...b, prop: 'nosignal' };
        case 'locked': return { ...b, antenna: 'off', prop: 'shackle', look: [0, -1] };
        case 'approving': return { ...b, glow: 1 };
        case 'finished': return { ...b, wink: t > 0.3 && t < 1.4 ? 1 : 0, happy: 0, glow: 1 };
        case 'wink': return { ...b, wink: t > 0.2 && t < 1.3 ? 1 : 0, glow: t < 1.3 ? 1 : 0, gazeW: 0.3 };
        default: return b;
      }
    },
    draw(p, c) {
      const back = canvas(), cv = canvas();
      const q = settle(p, (g, q) => ringBot(g, canvas(), q, c));
      const a = ringBot(cv, back, q, c);
      const t = q.propT;
      const [kx, ky] = a.knob;
      // Single cells show only where the circle allows.
      const dot = (into, x, y, color) => { if (safe(x, y)) into.put(x, y, color); };
      switch (q.prop) {
        case 'signal':
          if (kx !== undefined) {
            const k = Math.floor(t * 3) % 3;
            if (k >= 1) { dot(cv, kx + 3, ky - 1, INK); dot(cv, kx + 3, ky, INK); dot(cv, kx + 2, ky - 2, INK); }
            if (k >= 2) { dot(cv, kx + 5, ky - 2, INK); dot(cv, kx + 5, ky - 1, INK); dot(cv, kx + 5, ky, INK); dot(cv, kx + 4, ky - 3, INK); }
          }
          break;
        case 'zap':
          if (kx !== undefined) for (let i = 0; i < 3; i++) {
            const u = frac(steps(t, 8) * 1.2 + i / 3);
            const x = kx + 1 + rnd(u * 4) * [1, 1, 0][i], y = ky - rnd(u * 4) * [0, 1, 1][i];
            if (u < 0.8) dot(cv, clamp(x, 0, GRID - 1), Math.max(0, y), i === 1 ? '#F5BE2E' : INK);
          }
          break;
        case 'scan': {
          const row = a.cy - 3 + Math.floor(t * 4) % 7;
          for (let x = a.cx - 4; x <= a.cx + 4; x++) if (Math.hypot(x - a.cx, row - a.cy) < 4.4) dot(cv, x, row, '#7FB4FF');
          break;
        }
        case 'lens': {
          for (const [dx, dy] of [[5, 5], [6, 6], [7, 7], [6, 5], [7, 6]]) dot(cv, a.cx + dx, a.cy + dy, INK);
          for (const [dx, dy] of [[-2, -3], [-3, -2], [-3, -3]]) dot(cv, a.cx + dx, a.cy + dy, '#FFFFFF');
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
            const r = lerp(10, 5, u);
            dot(cv, rnd(a.cx + 0.5 + Math.sin(rad(ang)) * r), rnd(a.cy + 0.5 - Math.cos(rad(ang)) * r), INK);
          }
          break;
        case 'shackle': {
          // A padlock's shackle over the ring, so the whole bot reads as a lock.
          const steel = '#8D94A6';
          for (let x = a.cx - 2; x <= a.cx + 3; x++) dot(cv, x, a.cy - 7, steel);
          for (const x of [a.cx - 2, a.cx + 3]) dot(cv, x, a.cy - 6, steel);
          break;
        }
        case 'nosignal':
          if (kx !== undefined && Math.floor(t * 2.5) % 2) {
            for (const [dx, dy] of [[3, -2], [5, -2], [4, -1], [3, 0], [5, 0]]) dot(cv, kx + dx, ky + dy, '#D9534F');
          }
          break;
        default:
          pxProp(cv, back, q, a, pal, c);
      }
      return pixels(back, c, 0.7) + pixels(cv, c);
    },
  });
})();

// MARK: - Kimi Code · the moon rabbit
//
// The rabbit in the moon, for Moonshot: a white rabbit outlined in navy,
// blue inside its ears, hugging Kimi's blue dot like a little moon. Its
// ears do the talking: they twitch now and then, take turns dipping while
// it thinks and lie low while it sleeps. It works like the moon rabbit of
// the story, pounding mochi in a mortar beside it. Offline its dot goes
// dark and blinks; locked, the dot it hugs turns into a padlock.
// (v5.2, 2026-10-20.)
(() => {
  const NAVY = '#0B2C55', WHITE = '#FFFFFF', BLUE = '#2B8BFF', SHINE = '#7DB8FF', GLOW = '#BFE0FF', NOSE = '#FF7FA8', BLUSH = '#FFC2D6';
  const DIM_WHITE = '#D9DEE6', DIM_BLUE = '#8DA3BF', STEEL = '#8D94A6', STEEL_D = '#5E616C';
  const WOOD = '#B07A45', WOOD_D = '#7E5530', MOCHI = '#FFF8EE';
  const HEAD = [
    '......OWWOOOOWWO......',
    '.....OWWWWWWWWWWO.....',
    '....OWWWWWWWWWWWWO....',
    '....OWWWWWWWWWWWWO....',
    '....OWWWWWWWWWWWWO....',
    '....OWRWWWPPWWWRWO....',
    '.....OWWWWWWWWWWO.....',
    '.....OWWWSBBBWWWO.....',
    '.....OWWBSBBBBWWO.....',
    '......OWWBBBBWWO......',
    '.......OOOOOOOO.......',
  ];
  const TALL = ['.OO.', 'OWBO', 'OWBO', 'OWBO', 'OWWO'];
  const LOW = ['....', '....', '.OO.', 'OWBO', 'OWWO'];
  const flip = rows => rows.map(r => [...r].reverse().join(''));
  const MINI = ['#.#.', '#.#.', '####', '#bb#', '####'];
  function body(cv, p, c) {
    const x = rnd(p.dx), y = 3 + rnd(p.dy) - rnd(p.lift ?? 0);
    const dim = p.dim > 0.5;
    const dotOff = p.dotOff > 0.5 && Math.floor(c.T * 2) % 2;
    const lock = p.lockDot > 0.5;
    const MAP = { O: NAVY, W: dim ? DIM_WHITE : WHITE, B: lock ? STEEL : dim || dotOff ? DIM_BLUE : BLUE, S: lock ? '#B9BEC9' : dim || dotOff ? DIM_BLUE : SHINE, P: NOSE, R: BLUSH };
    art(cv, p.earL === 'low' ? LOW : TALL, x + 6, y, MAP);
    art(cv, flip(p.earR === 'low' ? LOW : TALL), x + 12, y, MAP);
    art(cv, HEAD, x, y + 5, MAP);
    // The dot it hugs glows while it thinks; locked, it's a padlock.
    if (p.glow && !dim) for (const [dx, dy] of [[10, 12], [11, 12], [12, 12], [10, 13], [11, 13]]) cv.put(x + dx, y + dy, GLOW);
    if (lock) { cv.put(x + 10, y + 11, STEEL_D); cv.put(x + 12, y + 11, STEEL_D); cv.put(x + 11, y + 13, NAVY); }
    const eyes = [[x + 8, y + 8], [x + 13, y + 8]];
    pxEyes(cv, eyes, c, p.eyes, NAVY);
    return { mid: x + 10, top: y, left: x + 4, right: x + 17, ground: y + 16, midY: y + 9, front: y + 10, eyes, x, y, hands: false };
  }
  const pal = { main: BLUE, body: WHITE, dark: NAVY, light: SHINE, accent: BLUE, mini: (g, x, y) => g.glyph(MINI, x, y, NAVY, { b: BLUE }) };
  lookBot('kimi', 'rabbit', 'Moon rabbit', 0x5AA6FF, body, pal, {
    fidgets: ['flop', 'hop', 'peek'],
    rest: { earL: 'tall', earR: 'tall', glow: 0, dotOff: 0, lockDot: 0 },
    poses(s, t, T, b) {
      // An ear twitch every few seconds of idle.
      const twitch = frac(T / 4.6) > 0.93;
      switch (s) {
        case 'idle': return { ...b, earR: twitch ? 'low' : 'tall' };
        case 'thinking': {
          const k = Math.floor(t / 0.45) % 4;
          return { ...b, prop: '', earL: k === 1 ? 'low' : 'tall', earR: k === 3 ? 'low' : 'tall', glow: Math.floor(t / 0.9) % 2, look: [k < 2 ? -1 : 1, -1], gazeW: 0 };
        }
        case 'working': return { ...b, dx: -1, prop: 'mochi', earL: 'low', earR: 'low', look: [1, 1] };
        case 'failed': return { ...b, earL: t > 0.3 ? 'low' : 'tall', earR: 'low' };
        case 'broken': return { ...b, earL: 'low', earR: 'low' };
        case 'offline': return { ...b, prop: '', dotOff: 1, earL: 'low' };
        case 'locked': return { ...b, prop: '', lockDot: 1, earL: 'low', earR: 'low', look: [0, 1] };
        case 'resting': return { ...b, earL: 'low', earR: 'low' };
        case 'sleeping': return { ...b, earL: 'low', earR: 'tall' };
        case 'finished': return { ...b, glow: t < 1.2 ? 1 : 0 };
        // Fidget: one ear flops over and springs back up, pleased.
        case 'flop': return { ...b, earR: t > 0.4 && t < 1.6 ? 'low' : 'tall', earL: t > 0.9 && t < 1.3 ? 'low' : 'tall', happy: t > 0.4 && t < 1.8 ? 1 : 0, gazeW: 0 };
        default: return b;
      }
    },
    props(cv, back, q, a, c) {
      if (q.prop !== 'mochi') return false;
      // A mortar right of it, the mallet up, then down on the mochi.
      const up = q.propU;
      const mx = 16, my = 14;
      art(cv, ['.M.', 'DDD', 'WWW', '.W.'], mx, my - 1, { M: MOCHI, D: WOOD_D, W: WOOD });
      if (up) {
        art(cv, ['H', 'H', 'H'], mx + 1, my - 8, { H: WOOD_D });
        for (const [dx, dy] of [[0, -6], [-1, -5], [-2, -4]]) cv.put(mx + dx, my + dy, WOOD);
      } else {
        art(cv, ['HHH', 'HHH'], mx, my - 4, { H: WOOD_D });
        for (const [dx, dy] of [[-1, -4], [-2, -4]]) cv.put(mx + dx, my + dy, WOOD);
        if (Math.floor(q.propT * 8) % 2) { cv.put(mx - 1, my - 2, '#FFD36B'); cv.put(mx + 3, my - 2, '#FFD36B'); }
      }
      return true;
    },
  });
})();

// MARK: - Kimi Code · the terminal pal
//
// Kimi Code as the terminal it lives in: a little blue monitor on stubby
// legs, block eyes on its navy screen, Kimi's dot on its antenna. Its
// screen does the talking: dots walk across it while it thinks, code
// scrolls under focused eyes while it works, it turns red when something
// fails, shows static offline and a padlock when locked, and asleep it
// goes to standby, the screen dark and the power light breathing amber.
// (v5.2, 2026-10-20.)
(() => {
  const BLUE = '#2B8BFF', SHINE = '#7DB8FF', DEEP = '#1F6FD6', NAVY = '#0B2C55', GLARE = '#1E4A80', EYE = '#FFFFFF';
  const LED = '#5BE37D', AMBER = '#F5BE2E', RED_LED = '#E5484D', DOT = '#0B2C55', LIT = '#BFE0FF', CODE = '#4F8FD9', DIMEYE = '#3A5A80';
  const DULL = '#7C93AE', DULL_D = '#61778F', RED = '#C7372F';
  const ART = [
    '...........ddd........',
    '...........ddd........',
    '...........ddd........',
    '............A.........',
    '.....LLLLLLLLLLLL.....',
    '....LBBBBBBBBBBBBB....',
    '....LBGNNNNNNNNNBB....',
    '....LBNNNNNNNNNNBB....',
    '....LBNNNNNNNNNNBB....',
    '....LBNNNNNNNNNNBB....',
    '....LBNNNNNNNNNNBB....',
    '....LBNNNNNNNNNNBB....',
    '....LBBBBBBBBBBBBB....',
    '....DBBBBBBBBBBEBD....',
    '.....DDDDDDDDDDDD.....',
    '.......NN....NN.......',
    '......NNN....NNN......',
  ];
  const MINI = ['####', '#nn#', '####', '#..#'];
  function body(cv, p, c) {
    const x = rnd(p.dx), y = 2 + rnd(p.dy) - rnd(p.lift ?? 0);
    const t = p.propT, T = c.T;
    const dim = p.dim > 0.5, red = p.screen === 'red';
    art(cv, ART, x, y, { L: dim ? DULL : SHINE, B: dim ? DULL : BLUE, D: dim ? DULL_D : DEEP, N: red ? RED : NAVY, G: red ? '#E0605A' : GLARE, E: LED, A: NAVY, d: p.dotLit ? LIT : DOT });
    if (!p.dotLit) cv.put(x + 11, y, dim ? DULL : SHINE);
    // Walking lifts a leg; tapping lifts the left one.
    const liftL = p.tap || p.legs === 'walk1' || p.legs === 'jump', liftR = p.legs === 'walk2' || p.legs === 'jump';
    if (liftL) { for (const i of [6, 7, 8]) cv.clear(x + i, y + 16); cv.put(x + 6, y + 15, NAVY); }
    if (liftR) { for (const i of [13, 14, 15]) cv.clear(x + i, y + 16); cv.put(x + 15, y + 15, NAVY); }
    // The power light: green, amber breathing in standby, red when locked or failed.
    cv.put(x + 15, y + 13, p.standby ? (Math.floor(T / 1.4) % 2 ? AMBER : DEEP) : p.screen === 'lock' || red ? RED_LED : p.screen === 'static' ? DEEP : LED);
    const eyes = [[x + 8, y + 7], [x + 12, y + 7]];
    switch (p.screen) {
      case 'dots': {
        // Thinking: the eyes become three dots, the lit one walking.
        const k = Math.floor(t * 4) % 3;
        for (let i = 0; i < 3; i++) cv.rect(x + 7 + i * 3, y + 8, 2, 2, i === k ? EYE : CODE);
        break;
      }
      case 'code':
        blockEyes(cv, eyes.map(([ex, ey]) => [ex, ey - 1]), c, 'focus', EYE);
        cv.rect(x + 7, y + 11, [5, 8, 3, 7, 6, 4][Math.floor(t * 6) % 6], 1, CODE);
        break;
      case 'static':
        // Offline: snow on the screen, no face.
        for (let j = 7; j <= 11; j++) for (let i = 6; i <= 15; i++) {
          const v = hash(i * 31 + j * 7 + Math.floor(T * 8) * 131);
          if (v > 0.62) cv.put(x + i, y + j, v > 0.85 ? '#C9D6E6' : '#55708F');
        }
        break;
      case 'lock':
        art(cv, ['.##.', '#..#', '####', '##.#', '####'], x + 9, y + 7, { '#': EYE });
        break;
      default:
        blockEyes(cv, eyes, c, p.standby ? 'closed' : p.eyes, p.standby ? DIMEYE : dim ? '#C9D6E6' : EYE);
    }
    return { mid: x + 10, top: y + 4, left: x + 4, right: x + 17, ground: y + 17, midY: y + 9, front: y + 10, eyes, hands: false };
  }
  const pal = { main: BLUE, body: BLUE, dark: NAVY, light: SHINE, accent: BLUE, mini: (g, x, y) => g.glyph(MINI, x, y, BLUE, { n: NAVY }) };
  lookBot('kimi', 'terminal', 'Terminal pal', 0x1F6FD6, body, pal, {
    fidgets: ['blip', 'hop', 'peek'],
    rest: { screen: '', standby: 0, dotLit: 0, tap: 0 },
    poses(s, t, T, b) {
      switch (s) {
        case 'idle': return { ...b, dotLit: frac(T / 6) > 0.92 ? 1 : 0 };
        case 'thinking': return { ...b, prop: '', screen: 'dots', dotLit: Math.floor(t * 2.5) % 2, look: [0, -1] };
        case 'writing': return { ...b, screen: 'code', tap: Math.floor(t * 8) % 2 };
        case 'working': return { ...b, dx: 0, prop: '', screen: 'code', tap: Math.floor(t * 4) % 2, dy: 0 };
        case 'failed': return { ...b, prop: '', screen: t < 1 && Math.floor(t * 6) % 2 ? 'red' : '', dim: 0 };
        case 'offline': return { ...b, prop: '', screen: 'static' };
        case 'locked': return { ...b, prop: '', screen: 'lock' };
        case 'resting': return { ...b, dy: 1, standby: 1, dim: 0 };
        case 'finished': return { ...b, dotLit: 1 };
        // Fidget: the antenna dot blips and it winks.
        case 'blip': return { ...b, dotLit: Math.floor(t * 4) % 2 && t < 1.6 ? 1 : 0, wink: t > 0.3 && t < 1.4 ? 1 : 0, gazeW: 0 };
        default: return b;
      }
    },
  });
})();

// MARK: - Grok · the event horizon
//
// Grok's ⊘ as a little black hole: a round black body, and the slash
// becomes its accretion disk, tilted across the front and glowing, its far
// side hidden behind the body. White pill eyes. The disk does the talking:
// a bright knot circles it slowly at idle and fast while it thinks, bits
// spiral in while it works, it flashes when it's done and cools to grey
// when it fails or sleeps. Offline the disk breaks into dashes; locked, a
// shackle over the top makes it a padlock. (v5.2, 2026-10-20.)
(() => {
  const INK = '#0B0B0C', HI = '#2A2A30', DISK = '#F2932B', HOT = '#FFD66B', KNOT = '#FFF6DA', EYE = '#FFFFFF';
  const COOL = '#8D8C94', COOL_HI = '#B9B9C0';
  const ANG = rad(-24);
  const MINI = ['.##.', '####', '####', '.##.'];
  function body(cv, p, c) {
    const cx = 10.5 + rnd(p.dx), cy = 10.5 + rnd(p.dy) - rnd(p.lift ?? 0);
    const R = 6.1, A = 9.1, B = 3.4;
    const cool = p.cool > 0.5 || p.dim > 0.5, flash = p.flash > 0.5;
    const knot = frac(c.T * (p.spin ?? 0.18));
    const tick = Math.floor(c.T * 4);
    for (let y = 0; y < GRID; y++) for (let x = 0; x < GRID; x++) {
      const X = x + 0.5, Y = y + 0.5;
      const u = (X - cx) * Math.cos(ANG) + (Y - cy) * Math.sin(ANG);
      const v = -(X - cx) * Math.sin(ANG) + (Y - cy) * Math.cos(ANG);
      const e = (u / A) ** 2 + (v / B) ** 2;
      const inDisk = e <= 1 && e >= 0.36, front = v > 0;
      const inOrb = Math.hypot(X - cx, Y - cy) <= R;
      // Where round the disk this cell sits, 0..1.
      const a = frac(Math.atan2(v / B, u / A) / TAU);
      const gap = p.dashed && (Math.floor(a * 14) + tick) % 2;
      if (inOrb && !(inDisk && front && !gap)) cv.put(x, y, Math.hypot(X - cx + 3, Y - cy + 3.2) < 1.2 ? HI : INK);
      else if (inDisk && !gap && (front || !inOrb) && safe(x, y)) {
        const near = Math.min(Math.abs(a - knot), 1 - Math.abs(a - knot)) < 0.06;
        cv.put(x, y, flash ? KNOT : cool ? (e < 0.6 ? COOL_HI : COOL) : near ? KNOT : e < 0.6 ? HOT : DISK);
      }
    }
    const ex = rnd(cx), ey = rnd(cy);
    const eyes = [[ex - 4, ey - 3], [ex + 1, ey - 3]];
    blockEyes(cv, eyes, c, p.eyes, p.dim > 0.5 ? '#CFCFCB' : EYE);
    return { mid: ex, top: ey - 7, left: ex - 7, right: ex + 7, ground: ey + 7, midY: ey, front: ey, eyes, cx, cy, hands: false };
  }
  const pal = { main: HOT, body: INK, dark: INK, light: COOL_HI, accent: INK, mini: (g, x, y) => { g.glyph(MINI, x, y, INK); g.rect(x - 1, y + 2, 6, 1, DISK); } };
  lookBot('grok', 'horizon', 'Event horizon', 0x3B3227, body, pal, {
    fidgets: ['spin', 'hop', 'peek'],
    rest: { spin: 0.18, cool: 0, flash: 0, dashed: 0 },
    poses(s, t, T, b) {
      switch (s) {
        case 'thinking': return { ...b, prop: '', spin: 1.1, look: [Math.sign(Math.sin(t * 1.4)), -1], gazeW: 0 };
        case 'working': return { ...b, dx: 0, prop: 'infall', spin: 0.6, eyes: 'focus', look: [0, 0] };
        case 'compacting': return { ...b, prop: 'infall', spin: 1.4 };
        case 'offline': return { ...b, prop: '', dashed: 1, spin: 0 };
        case 'locked': return { ...b, prop: 'shackle', look: [0, -1] };
        case 'resting': return { ...b, cool: 1, spin: 0 };
        case 'sleeping': return { ...b, cool: 1, spin: 0.05 };
        case 'finished': return { ...b, flash: t < 0.35 ? 1 : 0, spin: 1.4 };
        // Fidget: the knot races round the disk, pleased.
        case 'spin': return { ...b, spin: t < 2 ? 1.6 : 0.18, happy: t > 0.3 && t < 1.8 ? 1 : 0, gazeW: 0 };
        default: return b;
      }
    },
    props(cv, back, q, a, c) {
      if (q.prop === 'infall') {
        // Bits drawn in from outside, spiralling down into the body.
        for (let i = 0; i < 4; i++) {
          const u = frac(q.propT / 1.6 + i / 4);
          const ang = i * 90 + 40 + u * 200;
          const r = lerp(10, 6.5, u);
          const x = rnd(a.cx - 0.5 + Math.cos(rad(ang)) * r), y = rnd(a.cy - 0.5 + Math.sin(rad(ang)) * r * 0.85);
          if (safe(x, y) && !cv.has(x, y)) cv.put(x, y, i % 2 ? HOT : '#F4F4F1');
        }
        return true;
      }
      if (q.prop === 'shackle') {
        // A padlock's shackle over the top, so the whole bot reads as a lock.
        const steel = '#8D94A6';
        for (let x = a.mid - 3; x <= a.mid + 2; x++) if (safe(x, a.top - 1)) cv.put(x, a.top - 1, steel);
        for (const x of [a.mid - 3, a.mid + 2]) if (safe(x, a.top)) cv.put(x, a.top, steel);
        return true;
      }
      return false;
    },
  });
})();

// MARK: - Grok · the black cat
//
// Grok's wit as a black cat: pointed ears, gold eyes, a curl of tail, and
// the logo's slash as the white blaze on its chest. It acts like a cat: the
// tail sways at idle and flicks while it thinks, one paw raised to its
// chin; it kneads while it works and sleeps with its tail wrapped round.
// Offline its eyes dim and it looks about; locked, it wears a collar with a
// gold padlock tag. (v5.2, 2026-10-20.)
(() => {
  const INK = '#0B0B0C', EAR = '#3A3A42', GOLD = '#F5BE2E', DIM_GOLD = '#9C8A5A', WHITE = '#F4F4F1', NOSE = '#E8849A';
  const DIM_INK = '#4A4A52', COLLAR = '#D9534F';
  const ART = [
    '......K........K......',
    '.....KEK......KEK.....',
    '.....KEEKKKKKKEEK.....',
    '....KKKKKKKKKKKKKK....',
    '....KKKKKKKKKKKKKK....',
    '....KKKKKKKKKKKKKK....',
    '....KKKKKKKKKKKKKK....',
    '....KKKKKKKKKKKKKK....',
    '....KKKKKKKNKKKKKK....',
    '.....KKKKKKKKKKKK.....',
    '......KKKKKKKWKK......',
    '.....KKKKKKKWKKKK.....',
    '.....KKKKKKWKKKKK.....',
    '.....KKKKKWKKKKKKK....',
  ];
  const TAILS = {
    curl: [[18, 10], [18, 11], [17, 12], [18, 12]],
    sway: [[19, 9], [18, 10], [18, 11], [17, 12], [18, 12]],
    tip: [[17, 10], [18, 10], [18, 11], [17, 12], [18, 12]],
    up: [[18, 8], [18, 9], [18, 10], [18, 11], [17, 12], [18, 12]],
    low: [[17, 11], [17, 12]],
    wrap: [[17, 13], [16, 14], [15, 15], [14, 15], [13, 15], [12, 15], [11, 15], [10, 15], [9, 14]],
  };
  const MINI = ['#..#', '####', '#gg#', '####'];
  function body(cv, p, c) {
    const x = rnd(p.dx), y = 3 + rnd(p.dy) - rnd(p.lift ?? 0);
    const dim = p.dim > 0.5;
    const ink = dim ? DIM_INK : INK;
    art(cv, ART, x, y, { K: ink, E: EAR, W: WHITE, N: NOSE });
    // Walking lifts a paw; kneading takes turns; thinking holds one at its chin.
    const step = p.legs === 'walk1' ? 'L' : p.legs === 'walk2' ? 'R' : p.paw;
    if (step === 'chin') { cv.rect(x + 12, y + 10, 2, 2, WHITE); cv.rect(x + 14, y + 14, 2, 1, ink); }
    else cv.rect(x + 14, y + 14 + (step === 'R' ? -1 : 0), 2, 1, WHITE);
    cv.rect(x + 6, y + 14 + (step === 'L' ? -1 : 0), 2, 1, WHITE);
    for (const k of ['L', 'R']) if (step === k) cv.rect(x + (k === 'L' ? 6 : 14), y + 14, 2, 1, ink);
    for (const [dx, dy] of TAILS[p.tail] ?? TAILS.curl) cv.put(x + dx, y + dy, ink);
    if (p.collar) {
      cv.rect(x + 6, y + 9, 10, 1, COLLAR);
      art(cv, ['.##.', '#..#', 'GGGG', 'GKGG'], x + 9, y + 9, { '#': '#8D94A6', G: GOLD, K: INK });
    }
    const eyes = [[x + 7, y + 5], [x + 13, y + 5]];
    blockEyes(cv, eyes, c, p.eyes, dim || p.dull ? DIM_GOLD : GOLD);
    return { mid: x + 11, top: y, left: x + 4, right: x + 17, ground: y + 15, midY: y + 6, front: y + 9, eyes, hands: false };
  }
  const pal = { main: GOLD, body: INK, dark: INK, light: '#9A9AA0', accent: INK, mini: (g, x, y) => g.glyph(MINI, x, y, INK, { g: GOLD }) };
  lookBot('grok', 'cat', 'Black cat', 0x1A1A1F, body, pal, {
    fidgets: ['groom', 'swish', 'peek'],
    rest: { tail: 'curl', paw: '', collar: 0, dull: 0 },
    poses(s, t, T, b) {
      const sway = ['curl', 'sway', 'curl', 'tip'][Math.floor(T / 0.9) % 4];
      switch (s) {
        case 'idle': return { ...b, tail: sway };
        case 'thinking': return { ...b, prop: '', paw: 'chin', tail: Math.floor(t * 5) % 2 ? 'tip' : 'sway', look: [Math.sign(Math.sin(t * 1.2)), -1], gazeW: 0 };
        case 'working': {
          const k = Math.floor(t * 4) % 2;
          return { ...b, dx: 0, prop: '', paw: k ? 'L' : 'R', dy: k, eyes: 'focus', tail: 'curl', look: [0, 1] };
        }
        case 'failed': case 'broken': return { ...b, tail: 'low' };
        case 'offline': return { ...b, prop: '', dull: 1, tail: 'low' };
        case 'locked': return { ...b, prop: '', collar: 1, tail: 'tip', look: [0, 1] };
        case 'resting': return { ...b, dy: 0, tail: 'wrap' };
        case 'sleeping': return { ...b, tail: 'wrap' };
        case 'finished': return { ...b, tail: 'up' };
        // Fidgets: a lick of the paw, eyes shut; a swish of the tail.
        case 'groom': return { ...b, paw: t > 0.3 && t < 2.0 ? 'chin' : '', happy: t > 0.3 && t < 2.0 ? 1 : 0, tail: sway, gazeW: 0 };
        case 'swish': return { ...b, tail: t < 1.8 ? ['sway', 'tip', 'up', 'tip'][Math.floor(t * 6) % 4] : 'curl', look: [1, 0], gazeW: 0 };
        default: return { ...b, tail: sway };
      }
    },
  });
})();

// MARK: - Grok · the astronaut
//
// xAI's reach for space as a little astronaut: a round white helmet, the
// black visor is Grok's ⊘ with its slash as the glint, white pill eyes
// behind the glass, a small suit and backpack. The visor does the talking:
// the glint sweeps across now and then, dots walk on it while it thinks,
// it shows static offline, and asleep it goes dark. It works with a welding
// torch, sparks flying from its hand, plants a flag when it's done, and
// locked pulls a shutter over the visor, a padlock on its chest.
// (v5.2, 2026-10-20.)
(() => {
  const INK = '#0B0B0C', SUIT = '#F4F4F1', SHADE = '#CFCFCB', VISOR = '#16161A', GLINT = '#8D8C94', EYE = '#FFFFFF', PACK = '#9A9AA0';
  const DARK = '#2A2A30', FLAG = '#F5BE2E', DIM_SUIT = '#BDBDB8', SLAT = '#5E616C';
  const ART = [
    '........OOOOOO........',
    '......OOWWWWWWOO......',
    '.....OWWWWWWWWWWO.....',
    '....OWWVVVVVVVVWWO....',
    '...OWWVVVVVVVVVVWWO...',
    '...OWVVVVVVVVVVVVWO...',
    '...OWVVVVVVVVVVVVWO...',
    '...OWVVVVVVVVVVVVWO...',
    '...OWWVVVVVVVVVVWWO...',
    '...OSWWVVVVVVVVWWSO...',
    '....OSWWWWWWWWWWSO....',
    '......OOOOOOOOOO......',
    '....OOWWWWWWWWWWOO....',
    '....OWOWWWKKWWWOWO....',
    '.....OOWWWWWWWWOO.....',
    '......OOOO..OOOO......',
  ];
  const SLASH = [[6, 5], [7, 4], [8, 3], [5, 6]];
  const MINI = ['.##.', '#vv#', '#vv#', '.##.'];
  function body(cv, p, c) {
    const x = rnd(p.dx), y = 3 + rnd(p.dy) - rnd(p.lift ?? 0);
    const dim = p.dim > 0.5, T = c.T;
    art(cv, ART, x, y, { O: INK, W: dim ? DIM_SUIT : SUIT, S: SHADE, V: VISOR, P: PACK, K: INK });
    // Walking: a boot lifts.
    if (p.legs === 'walk1' || p.legs === 'jump') for (let i = 6; i <= 9; i++) cv.clear(x + i, y + 15);
    if (p.legs === 'walk2' || p.legs === 'jump') for (let i = 12; i <= 15; i++) cv.clear(x + i, y + 15);
    const eyes = [[x + 8, y + 5], [x + 12, y + 5]];
    if (p.visor === 'shut') {
      // A shutter over the visor, slats across it, and a padlock on the chest.
      for (let j = 3; j <= 9; j++) for (let i = 4; i <= 17; i++) if (cv.get(x + i, y + j) === VISOR) cv.put(x + i, y + j, j % 2 ? SLAT : GLINT);
      art(cv, ['.#.', 'GGG', 'GKG'], x + 10, y + 12, { '#': '#8D94A6', G: FLAG, K: INK });
    } else {
      // The glint: parked at the upper left, or sweeping across.
      const sweep = p.sweep >= 0 ? Math.floor(p.sweep * 14) - 3 : 0;
      for (const [dx, dy] of SLASH) {
        const gx = x + dx + sweep, gy = y + dy;
        if (cv.get(gx, gy) === VISOR) cv.put(gx, gy, GLINT);
      }
      if (p.visor === 'dots') {
        const k = Math.floor(p.propT * 4) % 3;
        for (let i = 0; i < 3; i++) cv.rect(x + 7 + i * 3, y + 6, 2, 2, i === k ? EYE : GLINT);
      } else if (p.visor === 'static') {
        for (let j = 4; j <= 9; j++) for (let i = 5; i <= 16; i++) {
          const v = hash(i * 31 + j * 7 + Math.floor(T * 8) * 131);
          if (v > 0.66 && cv.get(x + i, y + j) === VISOR) cv.put(x + i, y + j, v > 0.86 ? '#C9C9CF' : '#4A4A52');
        }
      } else if (p.visor === 'dark') blockEyes(cv, eyes, c, 'closed', DARK);
      else blockEyes(cv, eyes, c, p.eyes, dim ? '#CFCFCB' : EYE);
    }
    return { mid: x + 11, top: y, left: x + 3, right: x + 18, ground: y + 15, midY: y + 6, front: y + 9, eyes, hands: false };
  }
  const pal = { main: FLAG, body: SUIT, dark: INK, light: '#9A9AA0', accent: INK, mini: (g, x, y) => g.glyph(MINI, x, y, INK, { v: VISOR }) };
  lookBot('grok', 'astronaut', 'Astronaut', 0x5E616C, body, pal, {
    fidgets: ['float', 'glint', 'peek'],
    rest: { sweep: -1, visor: '' },
    poses(s, t, T, b) {
      const u = frac(T / 7);
      const sweep = u > 0.85 ? (u - 0.85) / 0.15 : -1;
      switch (s) {
        case 'idle': return { ...b, sweep };
        case 'thinking': return { ...b, prop: '', visor: 'dots' };
        case 'working': return { ...b, dx: 0, prop: 'weld', eyes: 'focus', look: [1, 1], sweep };
        case 'offline': return { ...b, prop: '', visor: 'static' };
        case 'locked': return { ...b, prop: '', visor: 'shut' };
        case 'resting': return { ...b, visor: 'dark', dy: 1 };
        case 'finished': return { ...b, prop: 'flag', sweep: t < 0.6 ? t / 0.6 : -1 };
        // Fidgets: a weightless drift up and down; the glint sweeps across.
        case 'float': {
          const d = t < 0.5 ? 0 : t < 1.0 ? -1 : t < 1.8 ? -2 : t < 2.3 ? -1 : 0;
          return { ...b, dy: d, legs: d < 0 ? 'jump' : 'stand', happy: d < -1 ? 1 : 0, gazeW: 0 };
        }
        case 'glint': return { ...b, sweep: t < 1.2 ? t / 1.2 : -1, look: [1, -1], gazeW: 0 };
        default: return { ...b, sweep };
      }
    },
    props(cv, back, q, a, c) {
      const pal2 = { main: FLAG, body: SUIT, dark: INK, light: '#9A9AA0', accent: INK, mini: () => {} };
      if (q.prop === 'weld') {
        // Sparks off the torch in its right hand.
        const f = Math.floor(q.propT * 10);
        cv.put(16, 14, '#5E616C');
        const [sx, sy] = [[17, 12], [18, 13], [17, 13]][f % 3];
        fitDraw(cv, g => { g.glyph(PIX.spark, sx, sy, '#FFD36B'); g.put(sx + 1, sy + 1, '#FFFFFF'); }, null);
        return true;
      }
      if (q.prop === 'flag') {
        // A flag planted beside it: the pole rises, then the flag unfurls.
        const t = q.propT, h = Math.min(6, Math.floor(t * 14));
        for (let i = 0; i < h; i++) cv.put(18, 15 - i, '#5E616C');
        if (h >= 6) art(cv, Math.floor(t * 4) % 2 ? ['###', '##.'] : ['###', '###'], 15, 10, { '#': FLAG });
        pxProp(cv, back, { ...q, prop: 'sparks' }, a, pal2, c);
        return true;
      }
      return false;
    },
  });
})();

// MARK: - Kimi Code · the moonlet
//
// Moonshot's crescent, chubby and blue, opening to the upper right, with
// the face on its thick belly, a blush, and Kimi's dot resting in the
// hollow like a little moon of its own. The dot does the talking: it bobs
// at idle, circles the hollow while it thinks, is tossed up and caught
// while it works, dims and blinks offline, and turns into a padlock when
// locked. (v5.2, 2026-10-20.)
(() => {
  const BLUE = '#2B8BFF', SHINE = '#7DB8FF', DEEP = '#1F6FD6', DOT = '#0B2C55', WHITE = '#FFFFFF', BLUSH = '#FF9EC4';
  const DULL = '#7C93AE', DULL_HI = '#A9BACD', DULL_D = '#61778F', STEEL = '#8D94A6', STEEL_D = '#5E616C', STAR = '#FFD66B';
  const ART = [
    '.....SSS........',
    '...SSBBBB.......',
    '..SBBBBBB.......',
    '.SBBBBBB........',
    '.SBBBBBB........',
    'SBBBBBBB........',
    'SBBBBBBBB.......',
    'SBBBBBBBBB......',
    'BBBBBBBBBBBB...B',
    'BBBBBBBBBBBBBBBB',
    'BBBBBBBBBBBBBBBB',
    '.DBBBBBBBBBBBBBD',
    '..DBBBBBBBBBBBD.',
    '...DDBBBBBBBDD..',
    '.....DDDDDDD....',
  ];
  const ROUND = ['.##.', '####', '####', '.##.'];
  // Where the dot sits, from the body's corner: at rest, round the hollow, tossed.
  const REST = [11, 3], ORBIT = [[11, 3], [12, 2], [11, 1], [10, 2]], TOSS = [[11, 4], [11, 2], [11, 0], [11, 2]];
  const MINI = ['.##.', '#...', '#...', '.##.'];
  function body(cv, p, c) {
    const x = 2 + rnd(p.dx), y = 3 + rnd(p.dy) - rnd(p.lift ?? 0);
    const dim = p.dim > 0.5;
    art(cv, ART, x, y, { B: dim ? DULL : BLUE, S: dim ? DULL_HI : SHINE, D: dim ? DULL_D : DEEP });
    const eyes = [[x + 4, y + 9], [x + 8, y + 9]];
    pxEyes(cv, eyes, c, p.eyes === 'open' ? 'wide' : p.eyes, WHITE);
    if (!dim) { cv.rect(x + 2, y + 12, 2, 1, BLUSH); cv.rect(x + 9, y + 12, 2, 1, BLUSH); }
    return { mid: x + 6, top: y, left: x, right: x + 15, ground: y + 15, midY: y + 10, front: y + 10, eyes, x, y, hands: false };
  }
  // The dot, drawn after the body so it can leave the hollow.
  function dot(cv, q, a, c) {
    const t = q.propT, T = c.T;
    let [dx, dy] = REST;
    if (q.dotPath === 'bob') dy += frac(T / 2.4) < 0.5 ? 0 : -1;
    else if (q.dotPath === 'orbit') [dx, dy] = ORBIT[Math.floor(t * 4) % 4];
    else if (q.dotPath === 'toss') [dx, dy] = TOSS[Math.floor(t * 4) % 4];
    else if (q.dotPath === 'twirl') [dx, dy] = t < 1.6 ? ORBIT[Math.floor(t * 5) % 4] : REST;
    const off = q.dotOff > 0.5 && Math.floor(T * 2) % 2;
    const lock = q.dotLock > 0.5, lit = q.dotLit > 0.5;
    const color = lock ? STEEL : off || q.dim > 0.5 ? DULL_D : lit ? STAR : DOT;
    fitDraw(cv, g => {
      g.glyph(ROUND, a.x + dx, a.y + dy, color);
      if (lock) { g.put(a.x + dx + 1, a.y + dy - 1, STEEL_D); g.put(a.x + dx + 2, a.y + dy - 1, STEEL_D); g.put(a.x + dx + 1, a.y + dy + 2, DOT); }
      else if (!off && !lit) g.put(a.x + dx + 1, a.y + dy + 1, '#24508A');
    }, null);
  }
  const pal = { main: BLUE, body: BLUE, dark: DOT, light: SHINE, accent: BLUE, mini: (g, x, y) => g.glyph(MINI, x, y, BLUE) };
  lookBot('kimi', 'moonlet', 'Moonlet', 0x4A9BFF, body, pal, {
    fidgets: ['twirl', 'hop', 'peek'],
    rest: { dotPath: 'still', dotOff: 0, dotLock: 0, dotLit: 0 },
    poses(s, t, T, b) {
      switch (s) {
        case 'idle': return { ...b, dotPath: 'bob' };
        case 'thinking': return { ...b, prop: '', dotPath: 'orbit', look: [1, -1], gazeW: 0 };
        case 'working': {
          const k = Math.floor(t * 4) % 4;
          return { ...b, dx: 0, prop: '', dotPath: 'toss', look: [1, k === 2 ? -1 : k === 0 ? 0 : -0.5], gazeW: 0 };
        }
        case 'offline': return { ...b, prop: '', dotOff: 1 };
        case 'locked': return { ...b, prop: '', dotLock: 1, look: [1, -1] };
        case 'finished': return { ...b, dotLit: t < 1.4 ? 1 : 0 };
        case 'listening': return { ...b, dotPath: 'bob' };
        // Fidget: the dot runs a lap of the hollow, and the moon smiles.
        case 'twirl': return { ...b, dotPath: 'twirl', happy: t > 0.3 && t < 1.8 ? 1 : 0, gazeW: 0 };
        default: return b;
      }
    },
    props(cv, back, q, a, c) {
      dot(cv, q, a, c);
      return false;
    },
  });
})();

// MARK: - Kimi Code · the moonshot rocket
//
// Moonshot, taken literally: a chubby blue rocket with navy nose and fins,
// its face looking out of the porthole. Its band and engine do the
// talking: lights count along the band while it thinks, the engine rumbles
// with puffs while it works, it lifts off on a flame to run and hops up on
// one when it's done. Offline its nose light blinks red; locked, a launch
// clamp holds it, padlocked. (v5.2, 2026-10-20.)
(() => {
  const BLUE = '#2B8BFF', SHINE = '#7DB8FF', NAVY = '#0B2C55', RING = '#CFE6FF', WHITE = '#FFFFFF', GLASS = '#E4F0FF', STEEL = '#8D94A6', STEEL_D = '#5E616C';
  const DULL = '#7C93AE', DULL_HI = '#A9BACD', LIGHT = '#5BE3FF', RED = '#E5484D';
  const ART = [
    '..........NN..........',
    '.........NNNN.........',
    '........NNNNNN........',
    '.......LBBBBBBB.......',
    '......LBBRRRRBBB......',
    '......LBRGWWWRBB......',
    '......LRWWWWWWRB......',
    '......LRWWWWWWRB......',
    '......LRWWWWWWRB......',
    '......LBRWWWWRBB......',
    '......LBBRRRRBBB......',
    '......NNNNNNNNNN......',
    '.....FLBBBBBBBBBF.....',
    '....FFLBBBBBBBBBFF....',
    '....FFF.SSSSSS.FFF....',
  ];
  const MINI = ['.#.', '###', '###', '#.#'];
  function body(cv, p, c) {
    const x = rnd(p.dx), y = 2 + rnd(p.dy) - rnd(p.lift ?? 0);
    const dim = p.dim > 0.5;
    art(cv, ART, x, y, { N: NAVY, B: dim ? DULL : BLUE, L: dim ? DULL_HI : SHINE, R: RING, W: dim ? '#D9DEE6' : WHITE, G: GLASS, F: NAVY, S: STEEL });
    // The nose light: red and blinking offline.
    if (p.noseRed && Math.floor(c.T * 2) % 2) { cv.put(x + 10, y, RED); cv.put(x + 11, y, RED); }
    // Thinking: lights count along the band.
    if (p.count) { const n = Math.floor(p.propT * 6) % 9; for (let i = 0; i < n; i++) cv.put(x + 7 + i, y + 11, LIGHT); }
    const eyes = [[x + 9, y + 6], [x + 12, y + 6]];
    pxEyes(cv, eyes, c, p.eyes, NAVY);
    return { mid: x + 11, top: y, left: x + 4, right: x + 17, ground: y + 15, midY: y + 7, front: y + 9, eyes, x, y, hands: false };
  }
  const pal = { main: BLUE, body: BLUE, dark: NAVY, light: SHINE, accent: BLUE, mini: (g, x, y) => g.glyph(MINI, x, y, BLUE) };
  lookBot('kimi', 'rocket', 'Rocket', 0x2B7BEF, body, pal, {
    fidgets: ['rumble', 'hop', 'peek'],
    rest: { count: 0, noseRed: 0, flame: 0, clamp: 0 },
    poses(s, t, T, b) {
      switch (s) {
        case 'thinking': return { ...b, prop: '', count: 1, look: [0, -1], gazeW: 0 };
        case 'working': return { ...b, dx: Math.floor(t * 8) % 2, dy: 0, prop: 'puffs', flame: 1, eyes: 'focus' };
        case 'running': return { ...b, prop: 'flame', flame: 2, lift: 1 + (Math.floor(t * 10) % 2), dy: 0, legs: 'stand' };
        case 'finished': return { ...b, prop: 'sparks', flame: t < 0.6 ? 2 : 0, lift: t < 0.6 ? 2 : 0, dy: 0 };
        case 'offline': return { ...b, prop: '', noseRed: 1 };
        case 'locked': return { ...b, prop: 'clamp', look: [0, -1] };
        case 'searching': return { ...b, legs: 'stand' };
        // Fidget: the engine coughs a puff and the rocket gives a little jump.
        case 'rumble': return { ...b, prop: t > 0.3 && t < 1.4 ? 'puffs' : '', flame: t > 0.3 && t < 1.4 ? 1 : 0, dx: t > 0.3 && t < 1.0 ? Math.floor(t * 8) % 2 : 0, happy: t > 1.0 && t < 1.8 ? 1 : 0, gazeW: 0 };
        default: return b;
      }
    },
    props(cv, back, q, a, c) {
      const t = q.propT;
      // The engine's flame under the nozzle: small, or a full burn.
      if (q.flame) {
        const big = q.flame > 1.5, flick = Math.floor(c.T * 12) % 2;
        fitDraw(cv, g => {
          g.rect(a.x + 9, a.y + 15, 4, 1, '#F5BE2E');
          g.rect(a.x + 10, a.y + 16, 2, 1 + (big ? 1 : 0) + flick, '#F2832B');
          if (big) g.put(a.x + 10 + flick, a.y + 18 + flick, '#F2483B');
        }, null);
      }
      if (q.prop === 'puffs') {
        const u = frac(t * 2);
        for (const side of [-1, 1]) fitDraw(back, g => g.glyph(PIX.puff, side < 0 ? a.left - 3 - rnd(u * 2) : a.right + rnd(u * 2), a.ground - 2, '#D9D3CF'), null);
        return true;
      }
      if (q.prop === 'clamp') {
        // A launch clamp round its middle, a padlock on it.
        cv.rect(a.x + 5, a.y + 8, 12, 2, STEEL);
        cv.rect(a.x + 5, a.y + 8, 12, 1, '#B9BEC9');
        art(cv, ['.#.', 'GGG', 'GKG'], a.x + 10, a.y + 10, { '#': STEEL_D, G: '#C9A23A', K: NAVY });
        return true;
      }
      return q.prop === 'flame';
    },
  });
})();

// MARK: - Grok · the Martian
//
// "Grok" is a Martian word (Heinlein's Stranger in a Strange Land), so Grok
// as a little Martian: a big pale head outlined in black, big black oval
// eyes with a glint, gold-tipped antennae, a small black suit. Its antennae
// do the talking: their tips glow in turn while it thinks, flash and arc
// while it works, droop while it sleeps, lose the signal offline (grey, one
// blinking red), and locked a shackle hangs between them over its head.
// (v5.2, 2026-10-20.)
(() => {
  const INK = '#0B0B0C', FACE = '#F4F4F1', SHADE = '#D9D8D2', GOLD = '#F5BE2E', DIM_GOLD = '#8C7A45', GLINT = '#FFFFFF';
  const DIM_FACE = '#CFCFCB', DIM_SHADE = '#B8B7B1', GREY = '#9A9AA0', RED = '#E5484D', STEEL = '#8D94A6';
  const ART = [
    '.....OOOOOOOOOOOO.....',
    '....OFFFFFFFFFFFFO....',
    '...OFFFFFFFFFFFFFFO...',
    '...OFFFFFFFFFFFFFFO...',
    '...OFFFFFFFFFFFFFFO...',
    '...OSFFFFFFFFFFFFSO...',
    '....OSFFFFFFFFFFSO....',
    '.....OSFFFFFFFFSO.....',
    '......OOSFFFFSOO......',
    '........OOOOOO........',
    '......OOOOGOOOOO......',
    '.....OO.OOOOOO.OO.....',
    '........OO..OO........',
  ];
  const MINI = ['#.#', '###', 'w#w', '###'];
  function eyesOf(cv, x, y, c, mode) {
    const e = c.eye;
    const lx = clamp(rnd(e.look[0] * 1.3), -1, 1), ly = clamp(rnd(e.look[1]), -1, 1);
    const m = moodOf(c, mode);
    [[x + 6, y + 5], [x + 12, y + 5]].forEach(([X, Y], i) => {
      const mm = i === 1 && e.wink > 0.5 ? 'happy' : m;
      const ink = { '#': INK };
      if (mm === 'happy') art(cv, ['.##.', '#..#'], X, Y + 1, ink);
      else if (mm === 'closed' || mm === 'blink' || mm === 'rest') art(cv, ['####'], X, Y + 2, ink);
      else if (mm === 'content') art(cv, ['#..#', '.##.'], X, Y + 1, ink);
      else if (mm === 'x') art(cv, ['#..#', '.##.', '#..#'], X, Y, ink);
      else if (mm === 'sad') art(cv, i ? ['##..', '###.', '####', '.##.'] : ['..##', '.###', '####', '.##.'], X, Y, ink);
      else if (mm === 'focus') art(cv, ['.##.', '####', '.##.'], X, Y + 1, ink);
      else {
        art(cv, ['.##.', '####', '####', '.##.'], X, Y, ink);
        cv.put(X + 1 + Math.max(0, lx), Y + 1 + Math.max(0, ly), GLINT);
      }
    });
  }
  function body(cv, p, c) {
    const x = rnd(p.dx), y = 3 + rnd(p.dy) - rnd(p.lift ?? 0);
    const dim = p.dim > 0.5, T = c.T;
    // Antennae: up, or drooping out to the sides.
    const tipColor = i => {
      if (p.tips === 'lost') return i === 0 && Math.floor(T * 2) % 2 ? RED : GREY;
      if (p.tips === 'turn') return Math.floor(p.propT * 2.5) % 2 === i ? GOLD : DIM_GOLD;
      if (p.tips === 'flash') return Math.floor(p.propT * 8) % 2 ? '#FFF2C2' : GOLD;
      if (p.tips === 'beep') return Math.floor(p.propT * 4) % 3 === i ? '#FFF2C2' : GOLD;
      return dim ? DIM_GOLD : GOLD;
    };
    if (p.ant === 'droop') {
      cv.put(x + 6, y + 2, INK); cv.rect(x + 4, y + 2, 2, 2, tipColor(0));
      cv.put(x + 15, y + 2, INK); cv.rect(x + 16, y + 2, 2, 2, tipColor(1));
    } else {
      cv.put(x + 7, y + 2, INK); cv.rect(x + 6, y, 2, 2, tipColor(0));
      cv.put(x + 14, y + 2, INK); cv.rect(x + 14, y, 2, 2, tipColor(1));
    }
    art(cv, ART, x, y + 3, { O: INK, F: dim ? DIM_FACE : FACE, S: dim ? DIM_SHADE : SHADE, G: dim ? DIM_GOLD : GOLD });
    eyesOf(cv, x, y, c, p.eyes);
    return { mid: x + 10, top: y, left: x + 3, right: x + 18, ground: y + 16, midY: y + 7, front: y + 10, eyes: [[x + 6, y + 6], [x + 14, y + 6]], x, y, hands: false };
  }
  const pal = { main: GOLD, body: FACE, dark: INK, light: GREY, accent: INK, mini: (g, x, y) => g.glyph(MINI, x, y, INK, { w: FACE }) };
  lookBot('grok', 'martian', 'Martian', 0x3A3A42, body, pal, {
    fidgets: ['beep', 'hop', 'peek'],
    rest: { ant: 'up', tips: '' },
    poses(s, t, T, b) {
      switch (s) {
        case 'idle': return { ...b, tips: frac(T / 5) > 0.9 ? 'turn' : '' };
        case 'thinking': return { ...b, prop: '', tips: 'turn', look: [Math.sign(Math.sin(t * 1.3)), -1], gazeW: 0 };
        case 'working': return { ...b, dx: 0, prop: 'arc', tips: 'flash', eyes: 'focus', dy: Math.floor(t * 4) % 2 };
        case 'resting': return { ...b, ant: 'droop' };
        case 'sleeping': return { ...b, ant: 'droop' };
        case 'broken': return { ...b, ant: 'droop' };
        case 'failed': return { ...b, ant: t > 0.4 ? 'droop' : 'up' };
        case 'offline': return { ...b, prop: '', tips: 'lost' };
        case 'locked': return { ...b, prop: 'shackle', look: [0, -1] };
        case 'finished': return { ...b, tips: t < 1.2 ? 'flash' : '' };
        // Fidget: the tips beep in turn, and it grins.
        case 'beep': return { ...b, tips: t < 1.8 ? 'beep' : '', happy: t > 0.4 && t < 1.8 ? 1 : 0, gazeW: 0 };
        default: return b;
      }
    },
    props(cv, back, q, a, c) {
      if (q.prop === 'arc') {
        // A spark arcing between the antenna tips.
        const k = Math.floor(q.propT * 10) % 3;
        for (let i = 8; i <= 13; i++) if ((i + k) % 3 === 0) cv.put(a.x + i, a.y + (i % 2), '#FFF2C2');
        return true;
      }
      if (q.prop === 'shackle') {
        for (let i = 8; i <= 13; i++) cv.put(a.x + i, a.y + 1, STEEL);
        cv.put(a.x + 8, a.y + 2, STEEL); cv.put(a.x + 13, a.y + 2, STEEL);
        return true;
      }
      return false;
    },
  });
})();

// MARK: - Looks
//
// Every look each agent can wear, its default first. Cursor shares Grok's
// subscription and draws Grok, so it shares Grok's looks too: the host
// passes Grok's picked look for both.

for (const [kind, id, name] of [
  ['claude', 'clawd', 'Clawd'],
  ['codex', 'notebook', 'Notebook'],
  ['antigravity', 'balloon', 'Balloon'],
  ['grok', 'ring', 'Slashed ring'],
  ['kimi', 'blob', 'Blob and terminal'],
  ['deepseek', 'whale', 'Whale'],
]) addLook(kind, id, name, CHARACTERS[kind], true);
LOOK_DEFS.cursor = LOOK_DEFS.grok;

// { kind: [{ id, name }] } for the pickers, each kind's default first
// (Phase 4's contract, LOOKS.md). Names are English and name the character,
// not the agent. Ids are saved in the user's settings: once shipped, an id
// never changes meaning and is never reused.
export const LOOKS = Object.freeze(Object.fromEntries(KINDS.map(kind =>
  [kind, Object.freeze((LOOK_DEFS[kind] ?? []).map(({ id, name }) => Object.freeze({ id, name })))])));
