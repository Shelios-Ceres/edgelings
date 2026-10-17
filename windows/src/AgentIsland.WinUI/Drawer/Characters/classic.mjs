// Agent Island - Classic characters
//
// An original, shippable character set for the Classic style, in the spirit
// of the app icon's dot bots: a soft, flat body you can reshape and recolour,
// two upright oval eyes, and a motion for every live phase.
//
// Pure JS, with no imports, DOM, timers or platform randomness: every random
// choice comes from the bot's seed, so the same seed and calls always draw
// the same frames. Frames stay inside the drawer's SvgCanvas subset: svg,
// defs, g, path (M L H V C A Z), circle and use; transform, opacity, fill,
// stroke, stroke-width, stroke-linecap and stroke-linejoin; #RRGGBB colours.

export const KINDS = ['claude', 'codex', 'antigravity', 'grok', 'cursor', 'kimi', 'deepseek'];

// The Pixel set's state names, so one phase mapping serves both styles.
export const PHASES = [
  'idle', 'thinking', 'streaming', 'reading', 'searching', 'web', 'writing', 'running',
  'controlling', 'spawning', 'orbit', 'sending', 'asking', 'approving', 'compacting',
  'working', 'finished', 'listening', 'failed', 'broken', 'offline', 'sleeping', 'locked',
  'resting',
];

// Slow enough to look right at the drawer's calm 30 fps.
export const CALM = ['idle', 'listening', 'broken', 'offline', 'sleeping', 'locked', 'resting'];

// One-shot entries: the state each settles into, and after how long.
const SETTLE = { finished: ['listening', 1.7], failed: ['broken', 1.5] };

const BLEND = 0.32;
const INK = '#1E1F25';
const PAPER = '#FFFFFF';

// MARK: - Math

const PI = Math.PI;
const TAU = PI * 2;
const rad = d => d * PI / 180;
const clamp = (v, a = 0, b = 1) => Math.min(b, Math.max(a, v));
const lerp = (a, b, t) => a + (b - a) * t;
const frac = v => v - Math.floor(v);
const smooth = t => { t = clamp(t); return t * t * (3 - 2 * t); };
const easeInOut = t => { t = clamp(t); return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2; };
const easeOut = t => 1 - Math.pow(1 - clamp(t), 3);
const easeIn = t => Math.pow(clamp(t), 3);
const bump = t => (t <= 0 || t >= 1) ? 0 : Math.sin(PI * t);
const wave = (t, period, phase = 0) => Math.sin(TAU * (t / period + phase));
const angleDelta = (a, b) => ((b - a) % 360 + 540) % 360 - 180;
// A damped spring kick: 0 at rest, positive first.
const kick = (q, freq = 14, decay = 6) => (q < 0 || q > 1.4) ? 0 : Math.sin(q * freq) * Math.exp(-q * decay);
// A back-out curve, 0 to 1 with a small overshoot.
const backOut = (t, s = 1.7) => { t = clamp(t) - 1; return t * t * ((s + 1) * t + s) + 1; };

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

// Positions round to 0.05 DIP (0.075 px at 150 %), so a resting bot repeats
// its text and the drawer skips re-rasterising it.
const n = v => {
  const r = Math.round(v * 20) / 20;
  return r === 0 ? '0' : String(r);
};
const n3 = v => {
  const r = Math.round(v * 1000) / 1000;
  return r === 0 ? '0' : String(r);
};
const O = o => (o >= 0.995 ? '' : ` opacity="${n3(clamp(o))}"`);
const matrix = (a, b, c, d, e, f) => ` transform="matrix(${n3(a)} ${n3(b)} ${n3(c)} ${n3(d)} ${n(e)} ${n(f)})"`;
const translate = (x, y) => (Math.abs(x) < 0.025 && Math.abs(y) < 0.025 ? '' : ` transform="translate(${n(x)} ${n(y)})"`);

const hexRgb = h => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const rgbHex = rgb => '#' + rgb.map(v => Math.round(clamp(v, 0, 255)).toString(16).padStart(2, '0')).join('').toUpperCase();
const mixHex = (a, b, t) => (t <= 0.002 ? a : t >= 0.998 ? b : rgbHex(hexRgb(a).map((v, i) => lerp(v, hexRgb(b)[i], t))));
const luma = h => { const [r, g, b] = hexRgb(h); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };

// An upright pill (an oval with straight sides), the dot bots' eye.
function pill(cx, cy, w, h) {
  const r = Math.min(w, h) / 2;
  const x0 = cx - w / 2, x1 = cx + w / 2, y0 = cy - h / 2, y1 = cy + h / 2;
  if (h <= w) return `M${n(x0 + r)} ${n(y0)}H${n(x1 - r)}A${n(r)} ${n(r)} 0 0 1 ${n(x1 - r)} ${n(y1)}H${n(x0 + r)}A${n(r)} ${n(r)} 0 0 1 ${n(x0 + r)} ${n(y0)}Z`;
  return `M${n(x0)} ${n(y0 + r)}A${n(r)} ${n(r)} 0 0 1 ${n(x1)} ${n(y0 + r)}V${n(y1 - r)}A${n(r)} ${n(r)} 0 0 1 ${n(x0)} ${n(y1 - r)}Z`;
}
const circle = (cx, cy, r, attrs) => `<circle cx="${n(cx)}" cy="${n(cy)}" r="${n(Math.max(0.05, r))}"${attrs}/>`;
const P = (d, attrs) => (d ? `<path d="${d}"${attrs}/>` : '');
const fill = c => ` fill="${c}"`;
const stroke = (c, w, cap = 'round') => ` fill="none" stroke="${c}" stroke-width="${n(w)}" stroke-linecap="${cap}" stroke-linejoin="round"`;
// A sticker: white fill, ink outline; reads on any wallpaper.
const sticker = (w = 1.1) => ` fill="${PAPER}" stroke="${INK}" stroke-width="${n(w)}" stroke-linejoin="round"`;

// MARK: - Poses

// Channels every pose carries. Numbers blend; props blend by name.
const REST = {
  x: 0, y: 0, sx: 1, sy: 1, rot: 0, turn: 0, spin: 0,
  look: [0, 0], gazeW: 1,
  open: 1, blink: 1, happy: 0, cross: 0, wide: 0, squint: 0, sleep: 0, sad: 0, wink: 0, mouth: 0, lid: 0,
  props: {},
};

function mixProps(a = {}, b = {}, w) {
  const out = {};
  for (const name of new Set([...Object.keys(a), ...Object.keys(b)])) {
    const pa = a[name], pb = b[name];
    if (pa && pb) {
      const m = {};
      for (const key in pb) m[key] = typeof pb[key] === 'number' && typeof pa[key] === 'number' ? lerp(pa[key], pb[key], w) : (w < 0.5 ? pa[key] ?? pb[key] : pb[key]);
      out[name] = m;
    } else if (pa) out[name] = { ...pa, a: (pa.a ?? 1) * (1 - w) };
    else out[name] = { ...pb, a: (pb.a ?? 1) * w };
  }
  return out;
}

function mixPoses(a, b, w) {
  const out = {};
  for (const key in b) {
    const va = a[key], vb = b[key];
    if (key === 'props') out.props = mixProps(va, vb, w);
    else if (typeof vb === 'number' && typeof va === 'number') out[key] = key === 'rot' || key === 'spin' ? va + angleDelta(va, vb) * w : lerp(va, vb, w);
    else if (Array.isArray(vb) && Array.isArray(va)) out[key] = vb.map((v, i) => lerp(va[i], v, w));
    else out[key] = w < 0.5 && va !== undefined ? va : vb;
  }
  return out;
}

// MARK: - Bots

export function createBot(kind, { seed = 1, tempo = 1, rim = true, shape, color } = {}) {
  const def = KIND[kind] ?? KIND.claude;
  const random = rng(seed);
  const k = { kind, seed, r: Array.from({ length: 24 }, random) };
  const look = { shape: shapeFor(shape) ?? def.shape, color: COLOR_HEX[color] ? color : def.color };
  let T = 40 + random() * 300;
  let cur = { state: 'idle', t: random() * 30 };
  let from = null;
  let w = 1;
  let gaze = null;
  const eyes = [0, 0];
  let lean = 0;
  let nextBlink = T + 0.8 + random() * 3;
  let blinkAt = -99;
  let pokeAt = -99;
  let rimOn = rim;
  let pose = null;

  const poseOf = s => {
    const p = stateOf(s.state)(s.t, T, k);
    return { ...REST, ...p, props: p.props ?? {} };
  };
  const blended = () => {
    const b = poseOf(cur);
    if (w >= 1 || !from) return b;
    const a = from.pose ?? poseOf(from.live);
    return mixPoses(a, b, easeInOut(w));
  };

  const bot = {
    kind,
    get state() { return cur.state; },
    get calm() { return CALM.includes(cur.state); },
    get shape() { return look.shape; },
    get color() { return look.color; },
    fidgets: def.fidgets,
    setState(state, phase = -1) {
      if (!STATES[state]) state = 'idle';
      if (state === cur.state && phase < 0) return;
      from = w < 1 ? { pose: blended() } : { live: { ...cur } };
      w = 0;
      const cycle = CYCLE[state] ?? 4;
      cur = { state, t: phase >= 0 ? phase * cycle : random() * cycle };
      if (SETTLE[state]) cur.t = 0;
    },
    setLook(nextShape, nextColor) {
      look.shape = shapeFor(nextShape) ?? look.shape;
      if (COLOR_HEX[nextColor]) look.color = nextColor;
    },
    setGaze(dx, dy) { gaze = [dx, dy]; },
    clearGaze() { gaze = null; },
    setRim(on) { rimOn = on; },
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
        nextBlink = T + (random() < 0.16 ? 0.3 : 2.2 + random() * 4.6);
      }
      pose = blended();
      // Eyes follow the pointer (saturating with distance), and the body
      // leans a little toward it.
      let target = pose.look, leanTo = 0;
      if (gaze) {
        const sat = v => v / Math.sqrt(1 + v * v);
        const g = [sat(gaze[0] / 60), sat(gaze[1] / 60)];
        target = [lerp(target[0], g[0], pose.gazeW), lerp(target[1], g[1], pose.gazeW)];
        leanTo = g[0] * pose.gazeW;
      }
      const follow = 1 - Math.exp(-dt * 12);
      eyes[0] += (target[0] - eyes[0]) * follow;
      eyes[1] += (target[1] - eyes[1]) * follow;
      lean += (leanTo - lean) * (1 - Math.exp(-dt * 6));
    },
    svg(size = 44, id = 'b') {
      if (!pose) pose = blended();
      const q = T - blinkAt;
      const blink = pose.blink > 0.5 && q < 0.16 ? bump(q / 0.16) : 0;
      // A click: a quick hop with a squash on take-off and landing.
      const p = T - pokeAt;
      const hop = p >= 0 && p < 0.55 ? bump(p / 0.42) * 5 : 0;
      const squash = p >= 0 && p < 0.55 ? kick(p, 22, 8) * 0.08 : 0;
      // A poke stirs a resting bot: its lids lift, then sink back.
      const stir = p >= 0 && p < 2.6 ? smooth(p / 0.12) * (1 - smooth((p - 1.1) / 1.5)) : 0;
      const live = {
        ...pose,
        y: pose.y - hop,
        sx: pose.sx * (1 + squash),
        sy: pose.sy * (1 - squash),
        rot: pose.rot + lean * 4,
        turn: clamp(pose.turn + lean * 0.22, -1, 1),
        look: [eyes[0], eyes[1]],
        open: pose.open * (1 - blink),
        lid: pose.lid * (1 - stir),
        happy: Math.max(pose.happy, p >= 0 && p < 0.5 ? bump(p / 0.5) : 0),
      };
      return draw(live, { T, id, rim: rimOn, shape: look.shape, color: look.color, k, size });
    },
  };
  pose = blended();
  return bot;
}

export const fidgetsOf = kind => (KIND[kind] ?? KIND.claude).fidgets;
export const settleOf = state => (SETTLE[state] ? SETTLE[state][0] : null);
export const swatchOf = kind => parseInt(COLOR_HEX[(KIND[kind] ?? KIND.claude).color].slice(1), 16);
export const lookOf = kind => ({ shape: (KIND[kind] ?? KIND.claude).shape, color: (KIND[kind] ?? KIND.claude).color });
export const cycleOf = state => CYCLE[state] ?? 4;

// MARK: - Shapes
//
// Twelve original body shapes, drawn for this set. Each sits in local
// coordinates around (0, 0), about 30 DIP across, with its bottom on y = 15
// (the base the body squashes and leans on). In the 44-DIP box it stands on
// its own baseline, base, as low as it can while every pose stays inside
// the drawer's circle; room is how far it can hop from there and top its
// highest point (check.mjs measures all three). face places the eyes: their
// centre, the gap between them and a size factor.

// A polygon with every corner rounded to radius r.
function roundedPolygon(points, r) {
  const m = points.length;
  let d = '';
  for (let i = 0; i < m; i++) {
    const p0 = points[(i + m - 1) % m], p1 = points[i], p2 = points[(i + 1) % m];
    const v1 = [p0[0] - p1[0], p0[1] - p1[1]], v2 = [p2[0] - p1[0], p2[1] - p1[1]];
    const l1 = Math.hypot(...v1), l2 = Math.hypot(...v2);
    const u1 = [v1[0] / l1, v1[1] / l1], u2 = [v2[0] / l2, v2[1] / l2];
    const angle = Math.acos(clamp(u1[0] * u2[0] + u1[1] * u2[1], -1, 1));
    const cut = r / Math.tan(angle / 2);
    const a = [p1[0] + u1[0] * cut, p1[1] + u1[1] * cut], b = [p1[0] + u2[0] * cut, p1[1] + u2[1] * cut];
    const sweep = u1[0] * u2[1] - u1[1] * u2[0] < 0 ? 1 : 0;
    d += `${i === 0 ? 'M' : 'L'}${n(a[0])} ${n(a[1])}A${n(r)} ${n(r)} 0 0 ${sweep} ${n(b[0])} ${n(b[1])}`;
  }
  return d + 'Z';
}

// Petals round a centre: each petal's outer arc runs between the notches
// it shares with its neighbours.
function flowerPath(petals, dist, r, start = -90) {
  const step = 360 / petals, half = rad(step / 2);
  const chord = 2 * dist * Math.sin(half);
  const mid = dist * Math.cos(half), h = Math.sqrt(r * r - (chord / 2) ** 2);
  const notch = mid + h;
  const at = deg => [notch * Math.cos(rad(deg)), notch * Math.sin(rad(deg))];
  let d = `M${at(start - step / 2).map(n).join(' ')}`;
  for (let i = 0; i < petals; i++) d += `A${n(r)} ${n(r)} 0 0 1 ${at(start + step / 2 + i * step).map(n).join(' ')}`;
  return d + 'Z';
}

const polyAround = (count, radius, start, dy = 0) =>
  Array.from({ length: count }, (_, i) => [radius * Math.cos(rad(start + i * 360 / count)), radius * Math.sin(rad(start + i * 360 / count)) + dy]);

const SHAPES = {
  blob: {
    d: 'M0 -14.5C8.5 -14.5 15.5 -8 15.5 1C15.5 9.5 9.5 15 0 15C-9.5 15 -15.5 9.5 -15.5 1C-15.5 -8 -8.5 -14.5 0 -14.5Z',
    top: -14.5, base: 39, room: 7.8,
    face: { x: 0, y: 1, gap: 9.4, es: 1 },
  },
  squircle: {
    d: 'M-14 -4.5C-14 -12 -12 -14 -4.5 -14H4.5C12 -14 14 -12 14 -4.5V6.5C14 13 12 15 4.5 15H-4.5C-12 15 -14 13 -14 6.5Z',
    top: -14, base: 37, room: 4.1,
    face: { x: 0, y: 0.5, gap: 9.6, es: 1 },
  },
  cloud: {
    d: 'M-8.5 15A7 7 0 0 1 -9.98 1.16A10 10 0 1 1 9.98 1.16A7 7 0 0 1 8.5 15Z',
    top: -9.47, base: 35.5, room: 9.3,
    face: { x: 0, y: 5, gap: 9.2, es: 0.95 },
  },
  sparkle: {
    d: 'M-2.4 -13.6A3.3 3.3 0 0 1 2.4 -13.6C4.95 -7.15 7.15 -4.95 13.6 -2.4A3.3 3.3 0 0 1 13.6 2.4C7.15 4.95 4.95 7.15 2.4 13.6A3.3 3.3 0 0 1 -2.4 13.6C-4.95 7.15 -7.15 4.95 -13.6 2.4A3.3 3.3 0 0 1 -13.6 -2.4C-7.15 -4.95 -4.95 -7.15 -2.4 -13.6Z',
    top: -14.64, base: 39, room: 7.6,
    face: { x: 0, y: 0.5, gap: 8.2, es: 0.84 },
  },
  flower: {
    d: flowerPath(6, 9, 6.5),
    top: -15.51, base: 39, room: 6.7,
    face: { x: 0, y: 0.5, gap: 9, es: 0.94 },
  },
  heart: {
    d: 'M0 -9.66A8 8 0 0 1 13.43 -1C10.43 4.2 4 11 1.4 13.6A2 2 0 0 1 -1.4 13.6C-4 11 -10.43 4.2 -13.43 -1A8 8 0 0 1 0 -9.66Z',
    top: -12.98, base: 39, room: 7.4,
    face: { x: 0, y: 0, gap: 9.4, es: 0.92 },
  },
  arch: {
    d: 'M-13 11V-2A13 13 0 0 1 13 -2V11A4 4 0 0 1 9 15H-9A4 4 0 0 1 -13 11Z',
    top: -15, base: 37, room: 5.3,
    face: { x: 0, y: 1, gap: 9.4, es: 1 },
  },
  wedge: {
    d: roundedPolygon([[0, -20], [16, 15], [-16, 15]], 4.6),
    top: -13.57, base: 37, room: 6.7,
    face: { x: 0, y: 6.5, gap: 8.6, es: 0.9 },
  },
  hex: {
    d: roundedPolygon(polyAround(6, 15.6, -90), 4.2),
    top: -14.96, base: 39, room: 7.3,
    face: { x: 0, y: 0.5, gap: 9.4, es: 0.96 },
  },
  teardrop: {
    d: 'M0 -15.5C2.5 -11.5 12 -6 12 3A12 12 0 0 1 -12 3C-12 -6 -2.5 -11.5 0 -15.5Z',
    top: -15.5, base: 39, room: 6.8,
    face: { x: 0, y: 4, gap: 9.2, es: 0.95 },
  },
  egg: {
    d: 'M0 -15C7.5 -15 12.5 -6 12.5 2C12.5 10 7 15 0 15C-7 15 -12.5 10 -12.5 2C-12.5 -6 -7.5 -15 0 -15Z',
    top: -15, base: 39, room: 7.3,
    face: { x: 0, y: 2.5, gap: 8.8, es: 0.95 },
  },
  bean: {
    d: 'M-14 4C-14 -6 -7 -9 -2 -6C1 -4 3 -4 6 -7C11 -10 15 -4 15 3C15 11 9 15 0 15C-8 15 -14 12 -14 4Z',
    top: -7.79, base: 39, room: 12,
    face: { x: 0.5, y: 4.5, gap: 9.2, es: 0.92 },
  },
};

export const SHAPE_NAMES = Object.keys(SHAPES);

// Shapes an earlier Classic build offered map to the nearest one here, so a
// saved look survives the switch.
const ALIAS = {
  square: 'squircle', pebble: 'egg', capsule: 'bean', star: 'sparkle', clover: 'flower',
  gem: 'hex', ring: 'blob', leaf: 'teardrop', shield: 'arch',
};
/** The shape drawn for a saved shape name, or null when there is none. */
export const shapeFor = name => (SHAPES[name] ? name : SHAPES[ALIAS[name]] ? ALIAS[name] : null);

// MARK: - Palette
//
// Eleven colours tuned for this set: bright enough to read as a toy on any
// wallpaper, and as an agent's colour on light and dark panels.

const COLOR_HEX = {
  black: '#24252C', brown: '#9C6B45', red: '#F04A55', orange: '#FF7A2E', yellow: '#FFB51F', green: '#22BF7A',
  cyan: '#17B5C7', violet: '#8E63F5', magenta: '#E9529E', gray: '#858A96', blue: '#2D8CFF',
};

export const COLORS = Object.keys(COLOR_HEX);
/** A colour's value as 0xRRGGBB, for DrawerLooks.Swatch. */
export const colorOf = name => parseInt((COLOR_HEX[name] ?? COLOR_HEX.blue).slice(1), 16);

// White eyes, or ink ones on a pale body.
const eyeOn = hex => (luma(hex) > 170 ? INK : PAPER);

// MARK: - Props
//
// Small sticker-style objects that say what a bot is doing: white with an
// ink outline, so they read on any wallpaper. Each draws in the 44-DIP box
// from its params; `a` is its opacity, blended across state changes.
//
// Everything stays within 22 DIP of the box's centre: the drawer's window
// region is each bot's inscribed circle, which is also all that takes clicks.
// Badges share one spot over the bot's right shoulder.

const BX = 32.8;
const BY = 10.8;

// A path builder that tracks the pen, so relative moves come out as the
// absolute commands SvgCanvas reads (M L H V C A Z).
function pen(x0, y0) {
  let cx = x0, cy = y0, d = `M${n(x0)} ${n(y0)}`;
  const api = {
    M(x, y) { cx = x; cy = y; d += `M${n(x)} ${n(y)}`; return api; },
    L(x, y) { cx = x; cy = y; d += `L${n(x)} ${n(y)}`; return api; },
    l(dx, dy) { return api.L(cx + dx, cy + dy); },
    h(dx) { cx += dx; d += `H${n(cx)}`; return api; },
    v(dy) { cy += dy; d += `V${n(cy)}`; return api; },
    C(x1, y1, x2, y2, x, y) { d += `C${n(x1)} ${n(y1)} ${n(x2)} ${n(y2)} ${n(x)} ${n(y)}`; cx = x; cy = y; return api; },
    c(x1, y1, x2, y2, x, y) { return api.C(cx + x1, cy + y1, cx + x2, cy + y2, cx + x, cy + y); },
    a(r, large, sweep, dx, dy) { cx += dx; cy += dy; d += `A${n(r)} ${n(r)} 0 ${large} ${sweep} ${n(cx)} ${n(cy)}`; return api; },
    Z() { d += 'Z'; return api; },
    get d() { return d; },
  };
  return api;
}

// A rounded rectangle, by its top-left corner.
const roundRect = (x, y, w, h, r) => pen(x + r, y).h(w - 2 * r).a(r, 0, 1, r, r).v(h - 2 * r).a(r, 0, 1, -r, r).h(-(w - 2 * r)).a(r, 0, 1, -r, -r).v(-(h - 2 * r)).a(r, 0, 1, r, -r).Z().d;

// An arc of a circle round (cx, cy), clockwise from angle a0 to a1 (degrees).
const arcPath = (cx, cy, r, a0, a1) => {
  const p0 = [cx + r * Math.cos(rad(a0)), cy + r * Math.sin(rad(a0))], p1 = [cx + r * Math.cos(rad(a1)), cy + r * Math.sin(rad(a1))];
  return `M${n(p0[0])} ${n(p0[1])}A${n(r)} ${n(r)} 0 ${a1 - a0 > 180 ? 1 : 0} 1 ${n(p1[0])} ${n(p1[1])}`;
};

const star4 = (cx, cy, r) => {
  const i = r * 0.38;
  return pen(cx, cy - r)
    .C(cx + i * 0.3, cy - i, cx + i, cy - i * 0.3, cx + r, cy)
    .C(cx + i, cy + i * 0.3, cx + i * 0.3, cy + i, cx, cy + r)
    .C(cx - i * 0.3, cy + i, cx - i, cy + i * 0.3, cx - r, cy)
    .C(cx - i, cy - i * 0.3, cx - i * 0.3, cy - i, cx, cy - r).Z().d;
};

// The burst's sparkle, at radius 10; each use scales it.
const STAR = star4(0, 0, 10);

// A stroke drawn twice, white under ink, so a line reads on any wallpaper.
const inked = (d, w = 1.3, o = 1) => (o < 0.02 ? '' : P(d, stroke(PAPER, w + 2) + O(o)) + P(d, stroke(INK, w) + O(o)));

// A mini dot bot, for spawning and orbiting.
function miniBot(x, y, s, c) {
  if (s <= 0.02) return '';
  const r = 4.6 * s, ew = 1.25 * s, eh = 2.2 * s, gap = 1.6 * s;
  return circle(x, y, r + 0.9, fill(PAPER)) + circle(x, y, r, fill(c.col))
    + P(pill(x - gap, y - 0.3 * s, ew, eh) + pill(x + gap, y - 0.3 * s, ew, eh), fill(c.eye));
}

const PROPS = {
  // Thinking: three bubbles rise one after another, up and to the right.
  dots: {
    layer: 'front',
    draw: p => [[28.2, 12.2, 1.1], [30.7, 9.7, 1.5], [33.6, 7.8, 2]].map(([x, y, r], i) => {
      const s = backOut(clamp(p.t * 3 - i * 0.6), 2) * (1 - smooth(clamp((p.t - 0.8) * 5)));
      return s > 0.02 ? circle(x, y, r * s, sticker(1)) : '';
    }).join(''),
  },
  // Replying: three short strokes fan out from the bot's side.
  lines: {
    layer: 'front',
    draw: p => [-1, 0, 1].map(i => {
      const q = frac(p.t + (i + 1) * 0.22), len = 1.4 + 1.6 * bump(q), x0 = 35.4 + q * 1.4, y0 = 22 + i * 4 + i * 0.6 * q;
      return inked(pen(x0, y0).l(len, i * 0.8).d, 1.3, bump(q));
    }).join(''),
  },
  // Reading: an open book held below the eyes.
  book: {
    layer: 'front',
    draw: p => {
      const y = p.y ?? 33, x = 22;
      const pages = pen(x, y - 2.2)
        .C(x - 2.6, y - 3.8, x - 6.4, y - 3.6, x - 8.6, y - 2.2).L(x - 8.6, y + 4.4)
        .C(x - 6.4, y + 3, x - 2.6, y + 3.2, x, y + 4.8)
        .C(x + 2.6, y + 3.2, x + 6.4, y + 3, x + 8.6, y + 4.4).L(x + 8.6, y - 2.2)
        .C(x + 6.4, y - 3.6, x + 2.6, y - 3.8, x, y - 2.2).Z().d;
      let text = '';
      for (const sd of [-1, 1]) for (const dy of [0, 1.9]) text += pen(x + sd * 2.2, y + dy - 0.4).l(sd * 4.2, 0).d;
      return P(pages, sticker(1.1)) + P(pen(x, y - 2.2).L(x, y + 4.8).d, stroke(INK, 0.9)) + P(text, stroke(INK, 0.8) + O(0.55));
    },
  },
  // Searching code: a magnifier, an open ring, sweeps a figure of eight in
  // front of the face; the eye it passes shows through.
  lens: {
    layer: 'front',
    draw: p => {
      const r = rad(p.r ?? 45), at = d => [p.x + Math.cos(r) * d, p.y + Math.sin(r) * d];
      const handle = pen(...at(4.2)).L(...at(7)).d;
      return circle(p.x, p.y, 4, stroke(PAPER, 3.4)) + P(handle, stroke(PAPER, 3.8))
        + P(handle, stroke(INK, 2)) + circle(p.x, p.y, 4, stroke(INK, 1.3)) + P(arcPath(p.x, p.y, 2.6, 200, 250), stroke(PAPER, 0.9));
    },
  },
  // Searching the web: a little globe turns over the shoulder. Its two
  // meridians sit a quarter turn apart and draw widest first, so a quarter
  // turn per cycle loops without a seam.
  globe: {
    layer: 'front',
    draw: p => {
      const R = 4.6, lon = rad(p.r ?? 0);
      const widths = [lon, lon + PI / 2].map(l => R * Math.abs(Math.sin(l))).sort((a, b) => b - a);
      const lines = widths.map(rx => (rx < 0.3 ? pen(BX, BY - R).L(BX, BY + R).d
        : `M${n(BX)} ${n(BY - R)}A${n(rx)} ${n(R)} 0 0 1 ${n(BX)} ${n(BY + R)}A${n(rx)} ${n(R)} 0 0 1 ${n(BX)} ${n(BY - R)}`)).join('');
      return circle(BX, BY, R, sticker(1.1)) + P(lines + pen(BX - R, BY).h(2 * R).d, stroke(INK, 0.9));
    },
  },
  // Editing code: a pencil writes a wavy line along the bottom, lifts and
  // starts again; the line fades as it goes back.
  pencil: {
    layer: 'front',
    draw: p => {
      const x = p.x, y = p.y, ang = rad(-52), ux = Math.cos(ang), uy = Math.sin(ang), nx = -uy * 1.8, ny = ux * 1.8;
      const at = (along, across) => [x + ux * along + nx * across, y + uy * along + ny * across];
      const body = pen(x, y).L(...at(3.2, 1)).L(...at(11, 1)).L(...at(11, -1)).L(...at(3.2, -1)).Z().d;
      let line = '';
      if (p.ink > p.x0 + 0.3) {
        const g = pen(p.x0, p.base);
        let up = true;
        for (let s = p.x0; s < p.ink - 0.05; s += 1.4) {
          const e = Math.min(p.ink, s + 1.4), h = up ? -1.3 : 1.3;
          g.C(s + (e - s) * 0.3, p.base + h, s + (e - s) * 0.7, p.base + h, e, p.base);
          up = !up;
        }
        line = inked(g.d, 1, p.fade ?? 1);
      }
      return line + P(body, sticker(1.1)) + P(pen(x, y).L(...at(3.2, 0)).d, stroke(INK, 1.4)) + P(pen(...at(9, 1)).L(...at(9, -1)).d, stroke(INK, 0.9));
    },
  },
  // Running commands: a tiny terminal with a blinking cursor.
  term: {
    layer: 'front',
    draw: p => {
      const x = 27, y = 29.5, on = frac(p.t + 0.2) < 0.55;
      return P(roundRect(x, y, 10, 7.5, 1.8), fill(INK) + ` stroke="${PAPER}" stroke-width="1.1"`)
        + P(pen(x + 2, y + 2.1).l(1.7, 1.6).l(-1.7, 1.6).d, stroke(PAPER, 1))
        + (on ? P(pen(x + 4.9, y + 5.3).l(2.8, 0).d, stroke('#8FE3B0', 1.2)) : '');
    },
  },
  // Operating apps: a pointer moves and clicks.
  cursor: {
    layer: 'front',
    draw: p => {
      const x = p.x, y = p.y, k = p.click ?? 0, s = 0.78 * (1 - 0.12 * k);
      const d = pen(x, y).l(0, 10 * s).l(2.6 * s, -2.4 * s).l(2 * s, 4.2 * s).l(1.8 * s, -0.9 * s).l(-2 * s, -4.1 * s).l(3.6 * s, -0.4 * s).Z().d;
      return (k > 0.02 ? circle(x, y, 2 + 4 * k, stroke(INK, 1) + O(1 - k)) : '') + P(d, sticker(1.1));
    },
  },
  // Starting a subagent: a mini bot pops out beside the bot.
  mini: { layer: 'front', draw: (p, c) => miniBot(p.x, p.y, p.s, c) },
  // Waiting on subagents: two minis orbit; drawn by depth (drawMinis).
  minis: { layer: 'split', draw: () => '' },
  // Sending: a paper plane, held, then thrown up and away.
  plane: {
    layer: 'front',
    draw: p => {
      const t = p.t;
      if (t >= 1 || (p.s ?? 1) < 0.05) return '';
      const x = 30.5 - t * 1.5, y = 19.5 - t * 13.5 - Math.sin(t * PI), s = (0.8 + t * 0.2) * (p.s ?? 1);
      const body = pen(x - 4.6 * s, y + 1.2 * s).L(x + 4.8 * s, y - 3.4 * s).L(x + 0.6 * s, y + 4.4 * s).L(x - 0.6 * s, y + 1.6 * s).Z().d;
      const fold = pen(x - 0.6 * s, y + 1.6 * s).L(x + 4.8 * s, y - 3.4 * s).d;
      const o = 1 - smooth(clamp((t - 0.5) * 2.8));
      return o < 0.02 ? '' : P(body, sticker(1.1) + O(o)) + P(fold, stroke(INK, 0.8) + O(o));
    },
  },
  // Asking you: a round speech bubble with a question mark, its tail
  // toward the bot's head.
  ask: {
    layer: 'front',
    draw: p => {
      const x = BX, y = p.y ?? BY, R = 4.8;
      const on = a => [x + R * Math.cos(rad(a)), y + R * Math.sin(rad(a))];
      const bubble = pen(...on(105)).a(R, 1, 0, on(135)[0] - on(105)[0], on(135)[1] - on(105)[1]).L(x - 5.2, y + 6.6).Z().d;
      const q = pen(x - 1.45, y - 1.9).a(1.55, 1, 1, 2.15, 1.4).c(-0.5, 0.25, -0.7, 0.6, -0.7, 1.1).d;
      return P(bubble, sticker(1.1)) + P(q, stroke(INK, 1.2)) + circle(x, y + 2.5, 0.65, fill(INK));
    },
  },
  // Waiting for your approval: a round badge with an exclamation mark.
  alert: {
    layer: 'front',
    draw: p => {
      const s = p.s ?? 1;
      return circle(BX, BY, 4.4 * s + 1.1, fill(INK)) + circle(BX, BY, 4.4 * s, fill('#FFC23D'))
        + P(pen(BX, BY - 2.5 * s).L(BX, BY + 0.7 * s).d, stroke(INK, 1.5 * s)) + circle(BX, BY + 2.3 * s, 0.85 * s, fill(INK));
    },
  },
  // Compacting: chevrons press in from both sides.
  squeeze: {
    layer: 'front',
    draw: p => {
      const o = 2.6 * p.s;
      return inked(pen(3.5 + o, 21).l(2.6, 3).l(-2.6, 3).d + pen(40.5 - o, 21).l(-2.6, 3).l(2.6, 3).d, 1.5);
    },
  },
  // Working: a little gear turns over the shoulder.
  gear: {
    layer: 'front',
    draw: p => {
      const teeth = 8, r0 = 3.7, r1 = 4.9;
      const pt = (rr, aa) => [BX + rr * Math.cos(aa), BY + rr * Math.sin(aa)];
      let g = null;
      for (let i = 0; i < teeth; i++) {
        const a = rad(p.r + i * 360 / teeth), w = rad(360 / teeth / 4);
        const p0 = pt(r0, a - 2 * w);
        g = g ? g.L(...p0) : pen(...p0);
        g.L(...pt(r1, a - w)).L(...pt(r1, a + w)).L(...pt(r0, a + 2 * w));
      }
      return P(g.Z().d, sticker(1.1)) + circle(BX, BY, 1.4, fill(INK));
    },
  },
  // Finished: little sparkles burst outward and fade. One star in defs,
  // placed six times, keeps the frame small.
  burst: {
    layer: 'front',
    draw: (p, c) => {
      if (p.t <= 0 || p.t >= 1) return '';
      let uses = '';
      [-62, -20, 22, 64, -110, 110].forEach((deg, i) => {
        const dist = 12 + easeOut(p.t) * 6, a = rad(deg - 90), s = (i < 4 ? 2.4 : 1.8) * bump(p.t);
        if (s > 0.05) uses += `<use href="#${c.id}k"${matrix(s / 10, 0, 0, s / 10, 22 + Math.cos(a) * dist, 22 + Math.sin(a) * dist)} stroke-width="${n(8 / s)}"/>`;
      });
      return uses && `<defs><path id="${c.id}k" d="${STAR}"/></defs><g${sticker(0.8)}>${uses}</g>`;
    },
  },
  // Failed: a puff of smoke rises and thins.
  puff: {
    layer: 'front',
    draw: p => {
      if (p.t <= 0 || p.t >= 1) return '';
      const x = 28, y = 12 - p.t * 3, s = 0.6 + easeOut(p.t) * 0.45, o = 1 - smooth(clamp((p.t - 0.45) * 2.4));
      if (o < 0.02) return '';
      const d = pen(x - 4.6 * s, y + 2 * s).a(2.4 * s, 0, 1, 0.6 * s, -4.4 * s).a(3.2 * s, 0, 1, 5.8 * s, -1 * s)
        .a(2.6 * s, 0, 1, 2.8 * s, 4.4 * s).a(2.2 * s, 0, 1, -1.6 * s, 1 * s).Z().d;
      return P(d, ` fill="#D5D8DE" stroke="${INK}" stroke-width="1" stroke-linejoin="round"` + O(o));
    },
  },
  // Network trouble: a round Wi-Fi badge, struck through.
  nowifi: {
    layer: 'front',
    draw: () => {
      const arcs = arcPath(BX, BY + 2, 3.2, 225, 315) + arcPath(BX, BY + 2, 1.8, 225, 315);
      return circle(BX, BY, 4.8, sticker(1.1)) + P(arcs, stroke(INK, 1.1)) + circle(BX, BY + 2.1, 0.65, fill(INK))
        + P(pen(BX - 3, BY - 2.6).L(BX + 3, BY + 3.2).d, stroke('#F04A55', 1.4));
    },
  },
  // Out of quota: Zs drift up, one after another.
  zzz: {
    layer: 'front',
    draw: p => [0, 0.33, 0.66].map((o, i) => {
      const q = frac(p.t + o), s = 0.6 + q * 0.35, x = 26.4 + q * 2.2 + i * 0.3, y = 12.5 - q * 6.8;
      return inked(pen(x - 1.8 * s, y - 1.8 * s).h(3.6 * s).l(-3.6 * s, 3.6 * s).h(3.6 * s).d, 1.1, bump(q));
    }).join(''),
  },
  // Sign-in needed: a padlock over the shoulder that rattles now and then.
  lock: {
    layer: 'front',
    draw: p => {
      const x = BX, y = BY + 1.2, r = rad(p.r ?? 0), co = Math.cos(r), si = Math.sin(r);
      const pt = (dx, dy) => [x + dx * co - dy * si, y + dx * si + dy * co];
      const shackle = pen(...pt(-2.3, -0.6)).L(...pt(-2.3, -2.4)).C(...pt(-2.3, -5.4), ...pt(2.3, -5.4), ...pt(2.3, -2.4)).L(...pt(2.3, -0.6)).d;
      const body = pen(...pt(-3.6, -0.6)).L(...pt(3.6, -0.6)).L(...pt(3.6, 4.4)).L(...pt(-3.6, 4.4)).Z().d;
      return P(shackle, stroke(PAPER, 3.4)) + P(shackle, stroke(INK, 1.4)) + P(body, sticker(1.1)) + circle(...pt(0, 1.9), 0.8, fill(INK));
    },
  },
  // Humming: music notes float up, one after another.
  note: {
    layer: 'front',
    draw: p => [0, 0.5].map((off, i) => {
      const q = frac(p.t + off), x = 26.4 + q * 2.4 + i * 0.8, y = 13 - q * 6, o = bump(q);
      if (o < 0.02) return '';
      return inked(pen(x + 1.3, y + 2.8).L(x + 1.3, y - 2.2).l(2.4, 0.9).d, 1.1, o) + circle(x, y + 3, 1.5, fill(INK) + O(o));
    }).join(''),
  },
};

// The orbiting pair, split by depth so the far one passes behind the bot.
// The ring is tilted round its waist: the near pass runs below the eyes.
function drawMinis(p, c, behind) {
  return [0, PI].map(off => {
    const th = p.th + off, z = Math.sin(th);
    if ((z < 0) !== behind) return '';
    return miniBot(22 + Math.cos(th) * 14.5, 30 + z * 5, (0.72 + z * 0.16) * (p.s ?? 1), c);
  }).join('');
}

// MARK: - States
//
// Each state is a pose over time: channels (see REST) and props. A looping
// state works in u, its place in the cycle (0 to 1), and every wave in it turns
// a whole number of times per cycle, so the loop meets itself. A one-shot
// state works in seconds from its start. k carries the bot's seeded randoms.

const CYCLE = {
  idle: 12, thinking: 2.4, streaming: 0.9, reading: 3.2, searching: 3.6, web: 2.4, writing: 1.6,
  running: 0.6, controlling: 2.8, spawning: 2.6, orbit: 3.2, sending: 1.8, asking: 2.4,
  approving: 1.6, compacting: 1.4, working: 2.4, listening: 4.8, broken: 5, offline: 3.6,
  sleeping: 4.2, locked: 3.2, resting: 8,
  bounce: 1.2, peek: 2.6, wiggle: 1.4, float: 3, hum: 2.4, stretch: 2.8, puff: 2.4, wink: 2.6, shuffle: 2,
};

const seg = (u, a, b) => clamp((u - a) / (b - a));
const S1 = u => Math.sin(TAU * u);
// Breathing: taller and a touch narrower, so the body keeps its volume.
const breathe = (v, amount) => ({ sy: 1 + amount * v, sx: 1 - amount * 0.6 * v });
// A hop of height h over q in [0, 1], shaped by gravity (up is negative).
const arc = (q, h) => (q <= 0 || q >= 1 ? 0 : -h * 4 * q * (1 - q));
// Contact with the ground: 1 at either end of a hop, gone a few hundredths in.
const contact = q => Math.exp(-Math.min(q, 1 - q) * 28);
// Hops over [a, b] of the cycle, one per height: a crouch before the first,
// a squash at every landing. Returns y and c, the squash (0 to 1).
function hops(u, a, b, heights, ramp = 0.08) {
  const span = (b - a) / heights.length;
  if (u < a) return { y: 0, c: smooth(seg(u, a - ramp, a)) };
  if (u >= b) return { y: 0, c: Math.exp(-(u - b) / span * 28) };
  const q = (u - a) / span, i = Math.min(heights.length - 1, Math.floor(q)), f = q - i;
  return { y: arc(f, heights[i]), c: contact(f) };
}
// A settling wobble that starts at u = a and is gone by the seam.
const settle = (u, a, cycle, freq = 16, decay = 7) => (u > a ? kick((u - a) * cycle, freq, decay) * (1 - seg(u, 0.9, 1)) : 0);
const squash = (c, amount) => ({ sy: 1 - amount * c, sx: 1 + amount * 0.7 * c });

const LOOP = {
  // Breathes, sways a little and glances about, two glances per cycle.
  idle(u, k) {
    const [r0, r1, r2, r3, r4, r5] = k.r;
    const shift = r5 * 0.08;
    const look = u < 0.16 + shift ? [0, 0]
      : u < 0.36 + shift ? [(r1 - 0.5) * 1.5, (r2 - 0.5) * 0.8]
        : u < 0.56 ? [0, 0]
          : u < 0.78 ? [(r3 - 0.5) * 1.5, -0.15 - r4 * 0.35]
            : [0, 0];
    return { ...breathe(Math.sin(TAU * u * 4), 0.022), rot: 1.5 * Math.sin(TAU * (u + r0)), look, turn: look[0] * 0.16 };
  },
  // Waiting for your reply: eyes on you, a slow tilt of the head.
  listening(u) {
    return { ...breathe(Math.sin(TAU * u * 2), 0.018), rot: 2.4 * S1(u), look: [0, -0.06], wide: 0.12 };
  },
  thinking(u) {
    return {
      rot: -2.5 + 1.6 * S1(u), y: -0.5 + 0.5 * Math.cos(TAU * u * 2),
      look: [0.42 + 0.14 * S1(u), -0.8], turn: 0.12, squint: 0.12, gazeW: 0.25,
      props: { dots: { t: u } },
    };
  },
  // Replying: three syllables a cycle.
  streaming(u) {
    const s = 0.5 - 0.5 * Math.cos(TAU * u * 3);
    return {
      y: -0.7 * s, sy: 1 + 0.025 * s, sx: 1 - 0.015 * s,
      look: [0.3, 0.05], turn: 0.14, mouth: 0.2 + 0.6 * s, gazeW: 0.6,
      props: { lines: { t: u } },
    };
  },
  // Two lines a cycle: the eyes sweep across, then flick back.
  reading(u) {
    const line = frac(u * 2), scan = line < 0.8 ? easeInOut(line / 0.8) : 1 - easeInOut((line - 0.8) / 0.2);
    return {
      look: [lerp(-0.55, 0.55, scan), 0.72], rot: lerp(-1.6, 1.6, scan), turn: lerp(-0.1, 0.1, scan), gazeW: 0.15,
      props: { book: { y: 34 } },
    };
  },
  // A magnifier sweeps a figure of eight; the eyes follow it.
  searching(u) {
    const a = S1(u), b = Math.sin(TAU * u * 2);
    return {
      rot: 2.2 * a, x: 0.8 * a, turn: 0.22 * a,
      look: [0.5 + 0.4 * a, 0.35 + 0.25 * b], wide: 0.25, gazeW: 0.2,
      props: { lens: { x: 27 + 4 * a, y: 29 + 2 * b, r: 45 + 8 * a } },
    };
  },
  // Watches a turning globe, glancing across and back.
  web(u) {
    const a = S1(u);
    return {
      ...breathe(Math.cos(TAU * u * 2), 0.02),
      look: [0.5 + 0.3 * a, -0.55], turn: 0.15 + 0.1 * a, rot: 1.2 * a, gazeW: 0.3,
      props: { globe: { r: u * 90 } },
    };
  },
  // Writes a line, lifts the pencil and starts again.
  writing(u) {
    const w = seg(u, 0, 0.78), back = seg(u, 0.78, 1);
    const x = u < 0.78 ? lerp(24.5, 32, w) : lerp(32, 24.5, easeInOut(back));
    const y = 36.6 - (u < 0.78 ? 0.8 * Math.abs(Math.sin(PI * w * 5)) : 2.2 * bump(back));
    return {
      rot: 2.5, x: 0.6, look: [lerp(0.1, 0.7, (x - 24.5) / 7.5), 0.75], gazeW: 0.15,
      props: { pencil: { x, y, x0: 24.5, ink: u < 0.78 ? x : 32, base: 37.6, fade: 1 - back } },
    };
  },
  // Jogs on the spot beside its terminal.
  running(u) {
    const h = hops(u, 0, 1, [2, 2]);
    return {
      y: h.y, rot: 3 * S1(u), ...squash(h.c, 0.05),
      look: [0.55, 0.42], squint: 0.2, gazeW: 0.2,
      props: { term: { t: u } },
    };
  },
  // A pointer travels and clicks twice a cycle.
  controlling(u) {
    const A = [31.5, 24], B = [31, 12.5], C = [24, 31.5];
    const go = (p, q, s) => [lerp(p[0], q[0], s), lerp(p[1], q[1], s)];
    let pt = A, click = 0;
    if (u < 0.08) pt = A;
    else if (u < 0.32) pt = go(A, B, easeInOut(seg(u, 0.08, 0.32)));
    else if (u < 0.45) { pt = B; click = bump(seg(u, 0.34, 0.44)); }
    else if (u < 0.68) pt = go(B, C, easeInOut(seg(u, 0.45, 0.68)));
    else if (u < 0.8) { pt = C; click = bump(seg(u, 0.69, 0.79)); }
    else pt = go(C, A, easeInOut(seg(u, 0.8, 1)));
    return {
      look: [clamp((pt[0] - 22) / 12, -1, 1), clamp((pt[1] - 25) / 10, -1, 1)], turn: 0.18, rot: (pt[1] - 26) * -0.25,
      gazeW: 0.15, ...squash(click, 0.03),
      props: { cursor: { x: pt[0], y: pt[1], click } },
    };
  },
  // A crouch, a pop, and a little helper bounces beside it.
  spawning(u) {
    const pre = bump(seg(u, 0, 0.2)), pop = seg(u, 0.18, 0.38), gone = seg(u, 0.84, 1);
    const s = backOut(pop, 2.2) * (1 - easeIn(gone));
    const hop = u > 0.42 && u < 0.78 ? arc(frac(seg(u, 0.42, 0.78) * 2), 3) : 0;
    return {
      sy: 1 - 0.08 * pre + 0.04 * bump(pop), sx: 1 + 0.05 * pre - 0.02 * bump(pop),
      look: [0.7, 0.45], turn: 0.2, gazeW: 0.2,
      happy: smooth(seg(u, 0.2, 0.3)) * (1 - smooth(seg(u, 0.72, 0.84))),
      props: { mini: { x: 33, y: 33.4 + hop, s: s * 0.88 } },
    };
  },
  // Two helpers circle it; the eyes follow whichever is in front.
  orbit(u) {
    // Offset, so the helpers swap depth mid-cycle rather than at the seam.
    const th = TAU * (u + 0.08), front = Math.sin(th) >= 0 ? th : th + PI;
    return {
      ...breathe(Math.cos(TAU * u * 2), 0.015),
      look: [Math.cos(front) * 0.75, 0.45], turn: Math.cos(front) * 0.18, gazeW: 0.2,
      props: { minis: { th, s: 1 } },
    };
  },
  // Winds up, throws a paper plane and watches it go.
  sending(u) {
    const back = smooth(seg(u, 0.06, 0.26)) * (1 - smooth(seg(u, 0.26, 0.34)));
    const fwd = smooth(seg(u, 0.26, 0.34)) * (1 - smooth(seg(u, 0.4, 0.75)));
    const t = seg(u, 0.3, 0.95), watch = t * (1 - smooth(seg(u, 0.9, 1)));
    return {
      rot: -6 * back + 5 * fwd, x: -0.8 * back + 0.8 * fwd, sy: 1 - 0.05 * back + 0.03 * fwd,
      look: [0.45, -0.3 - 0.6 * watch], wide: 0.2 * fwd, gazeW: 0.2,
      props: { plane: { t, s: smooth(seg(u, 0, 0.12)) } },
    };
  },
  // Head tilted, eyes wide, a question bubble bobbing above.
  asking(u) {
    return {
      rot: -7 + 2 * S1(u), y: -0.5 + 0.5 * Math.cos(TAU * u * 2),
      look: [0.35, -0.35], wide: 0.25, turn: 0.1, gazeW: 0.7,
      props: { ask: { y: BY + 0.6 * S1(u) } },
    };
  },
  // Eager little hops under a pulsing badge, eyes on you.
  approving(u) {
    const h = hops(u, 0, 1, [2.6, 2.6]);
    return {
      y: h.y, ...squash(h.c, 0.05),
      look: [0, 0.05], wide: 0.3, gazeW: 1,
      props: { alert: { s: 1 + 0.1 * bump(frac(u * 2)) } },
    };
  },
  // Squeezed thin from both sides, then springs back.
  compacting(u) {
    const sq = easeInOut(seg(u, 0, 0.45)) * (1 - easeOut(seg(u, 0.55, 0.7)));
    const wob = settle(u, 0.6, CYCLE.compacting);
    return {
      sx: 1 - 0.14 * sq + 0.05 * wob, sy: 1 + 0.08 * sq - 0.04 * wob,
      squint: 0.7 * sq, gazeW: 0.3,
      props: { squeeze: { s: sq } },
    };
  },
  working(u) {
    const s = Math.sin(TAU * u * 2);
    return {
      rot: 2.5 * s, y: -0.8 * Math.abs(s),
      look: [0.35, -0.45], squint: 0.25, turn: 0.1, gazeW: 0.3,
      props: { gear: { r: u * 90 } },
    };
  },
  // Slumped, eyes crossed, with a twitch now and then.
  broken(u) {
    const twitch = settle(u, 0.62, CYCLE.broken, 20, 7);
    return {
      sy: 0.94 + 0.01 * S1(u), sx: 1.04, rot: -4 + S1(u) + 2 * twitch, y: 0.4,
      cross: 1, blink: 0, look: [0, 0.2], gazeW: 0,
    };
  },
  offline(u) {
    return {
      rot: -2 + 1.4 * S1(u), sy: 0.97 + 0.01 * Math.sin(TAU * u * 2), sad: 1, look: [-0.1, 0.35], gazeW: 0.3,
      props: { nowifi: { a: 0.8 + 0.2 * Math.cos(TAU * u) } },
    };
  },
  sleeping(u) {
    return {
      ...breathe(S1(u), 0.035), rot: -3, y: 0.4, sleep: 1, blink: 0, gazeW: 0,
      props: { zzz: { t: u } },
    };
  },
  // Looks up at its padlock, which rattles once a cycle.
  locked(u) {
    const jig = kick(u * CYCLE.locked, 18, 5);
    return {
      look: [0.55, -0.55], sad: 0.45, rot: 1.5 * jig, sy: 0.98, gazeW: 0.2,
      props: { lock: { r: 14 * jig } },
    };
  },
  // Its app is closed or out of sight: settled low and round, two slow
  // breaths a cycle, heavy lids over lowered eyes, and one slow blink away
  // from the start, so a still at u = 0 shows the settled pose. No badge:
  // nothing is wrong. Unlike sleeping, the eyes stay half open.
  resting(u, k) {
    const b = Math.sin(TAU * u * 2);
    const close = bump(seg(u, 0.56, 0.7));
    return {
      y: 0.4, sy: 0.965 + 0.026 * b, sx: 1.03 - 0.016 * b, rot: 0.8 * Math.sin(TAU * (u + k.r[0])),
      lid: 1, open: 1 - close, blink: 0, look: [0, 0.35], gazeW: 0.45,
    };
  },

  // Fidgets: short idle habits the drawer starts now and then. Each is play:
  // happy or curious eyes, never a work prop, a badge or a work motion, and
  // never closed eyes, which resting and sleeping use.
  bounce(u) {
    const h = hops(u, 0.08, 0.72, [4.5, 2.6]);
    const w = settle(u, 0.72, CYCLE.bounce, 18, 7);
    return { y: h.y, sy: 1 - 0.07 * h.c - 0.03 * w, sx: 1 + 0.05 * h.c + 0.02 * w, happy: 0.85 };
  },
  // Puffs up round, eyes wide with the held breath, then lets it out with a
  // grin and a wobble.
  puff(u) {
    const full = easeOut(seg(u, 0.08, 0.34)) * (1 - easeIn(seg(u, 0.56, 0.66)));
    const out = smooth(seg(u, 0.56, 0.62)) * (1 - smooth(seg(u, 0.84, 0.96)));
    const wob = settle(u, 0.64, CYCLE.puff, 16, 6);
    return {
      sx: 1 + 0.07 * full + 0.03 * wob, sy: 1 + 0.06 * full - 0.03 * wob, y: -0.5 * full,
      wide: 0.35 * full, look: [0, -0.25 * full], happy: 0.9 * out, mouth: 0.3 * out, gazeW: 0.3,
    };
  },
  // Tips its head, winks, and straightens up again.
  wink(u) {
    const tilt = smooth(seg(u, 0.06, 0.24)) * (1 - smooth(seg(u, 0.72, 0.9)));
    const w = smooth(seg(u, 0.3, 0.38)) * (1 - smooth(seg(u, 0.56, 0.64)));
    return { rot: 7 * tilt, turn: 0.16 * tilt, look: [0.3 * tilt, -0.1 * tilt], wink: w, gazeW: 0.6 };
  },
  // A little side-step dance: a step each way with a bob between, eyes happy.
  shuffle(u) {
    const s = Math.sin(TAU * u);
    return {
      x: 1.5 * s, rot: -5 * s, y: -0.9 * Math.abs(Math.sin(TAU * u * 2)),
      ...breathe(-Math.abs(Math.sin(TAU * u * 2)), 0.03), happy: 0.9, gazeW: 0.3,
    };
  },
  peek(u) {
    const l = smooth(seg(u, 0.05, 0.2)) * (1 - smooth(seg(u, 0.42, 0.55)));
    const r = smooth(seg(u, 0.55, 0.7)) * (1 - smooth(seg(u, 0.88, 1)));
    return {
      x: 1.6 * (r - l), rot: 6 * (r - l), turn: 0.4 * (r - l),
      look: [0.9 * (r - l), -0.2 * (l + r)], wide: 0.3 * (l + r), gazeW: 0.2,
    };
  },
  wiggle(u) {
    const env = Math.sin(PI * u);
    return { rot: 7 * Math.sin(TAU * u * 4) * env, y: -env, ...breathe(-env, 0.03), happy: 0.9, gazeW: 0.3 };
  },
  float(u) {
    return { y: -2.6 * (1 - Math.cos(TAU * u)), rot: 3 * S1(u), ...breathe(0.5, 0.03), happy: 0.9, look: [0, -0.2], gazeW: 0.3 };
  },
  hum(u) {
    const s = Math.sin(TAU * u * 2);
    return { rot: 5 * s, y: -1.2 * Math.abs(s), happy: 0.85, mouth: 0.22, gazeW: 0.2, props: { note: { t: u } } };
  },
  // Reaches up tall, sways once each way and shakes it out, grinning.
  stretch(u) {
    const up = smooth(seg(u, 0.05, 0.3)) * (1 - smooth(seg(u, 0.7, 0.85)));
    const side = Math.sin(TAU * seg(u, 0.25, 0.7));
    const shake = settle(u, 0.85, CYCLE.stretch, 18, 6);
    return {
      sy: 1 + 0.12 * up - 0.05 * shake, sx: 1 - 0.08 * up + 0.04 * shake, rot: 6 * side * up,
      happy: 0.9 * up, look: [0, -0.4 * up], mouth: 0.25 * up,
    };
  },
};

const ONCE = {
  // A crouch, a jump for joy, a squashy landing and a burst of sparkles.
  finished(t) {
    const crouch = smooth(seg(t, 0, 0.16)) * (1 - smooth(seg(t, 0.16, 0.22)));
    const q = seg(t, 0.18, 0.62), up = t > 0.18 && t < 0.62 ? 0.08 * (1 - q) : 0;
    const land = t < 0.62 ? 0 : t < 0.66 ? smooth((t - 0.62) / 0.04) : Math.exp(-(t - 0.66) * 14);
    const wob = t > 0.66 ? kick(t - 0.66, 16, 6) : 0;
    return {
      y: arc(q, 9),
      sy: 1 - 0.12 * crouch + up - 0.12 * land - 0.03 * wob,
      sx: 1 + 0.07 * crouch - 0.6 * up + 0.08 * land + 0.02 * wob,
      happy: smooth(seg(t, 0.16, 0.3)), mouth: 0.45 * smooth(seg(t, 0.18, 0.3)) * (1 - smooth(seg(t, 1.1, 1.5))),
      look: [0, -0.2 * bump(q)], gazeW: 0.5,
      props: { burst: { t: seg(t, 0.32, 1.25) } },
    };
  },
  // A flinch, a shudder, eyes crossed, a puff of smoke; it slumps into broken.
  failed(t) {
    const flinch = smooth(seg(t, 0, 0.08)) * (1 - smooth(seg(t, 0.1, 0.3)));
    const shudder = t > 0.12 ? Math.sin((t - 0.12) * 42) * Math.exp(-(t - 0.12) * 3.2) : 0;
    const slump = smooth(seg(t, 0.7, 1.5));
    return {
      x: 2.2 * shudder, rot: 3 * shudder - 4 * slump, y: 0.4 * slump,
      sy: 1 - 0.1 * flinch - 0.06 * slump, sx: 1 + 0.06 * flinch + 0.04 * slump,
      wide: 0.6 * flinch, cross: smooth(seg(t, 0.14, 0.24)), blink: 0, look: [0, 0.2 * slump], gazeW: 0,
      props: { puff: { t: seg(t, 0.18, 1.3) } },
    };
  },
};

const STATES = {};
for (const [name, f] of Object.entries(LOOP)) STATES[name] = (t, T, k) => f(frac(t / CYCLE[name]), k);
for (const [name, f] of Object.entries(ONCE)) STATES[name] = (t, T, k) => f(t, k);
const stateOf = name => STATES[name] ?? STATES.idle;

// MARK: - Kinds
//
// Each agent's default look and idle habits. The three blue agents (Codex,
// Kimi Code and DeepSeek) tell apart by silhouette: a cloud, a round blob
// and a drop.

const KIND = {
  claude: { shape: 'sparkle', color: 'orange', fidgets: ['hum', 'peek'] },
  codex: { shape: 'cloud', color: 'blue', fidgets: ['bounce', 'puff'] },
  antigravity: { shape: 'arch', color: 'violet', fidgets: ['float', 'wink'] },
  grok: { shape: 'wedge', color: 'gray', fidgets: ['peek', 'wiggle'] },
  cursor: { shape: 'hex', color: 'black', fidgets: ['shuffle', 'stretch'] },
  kimi: { shape: 'blob', color: 'blue', fidgets: ['bounce', 'wiggle'] },
  deepseek: { shape: 'teardrop', color: 'blue', fidgets: ['float', 'puff'] },
};

// MARK: - Drawing
//
// A frame, back to front: props behind the bot, then the body (a white rim
// under the fill, the fill and the face), then props in front. The body is
// one path in defs, used twice, so the drawer parses its geometry once and
// keeps it.

const BASE_X = 22;
const EYE_W = 4.8;
const EYE_H = 7.8;

// A closed eye: an arc that bulges up when happy (lift > 0) and down when
// asleep (lift < 0), flat in between.
function arcEye(x, y, w, lift, col, sw) {
  const half = w / 2, h = Math.abs(lift);
  if (h < 0.05) return `M${n(x - half)} ${n(y)}H${n(x + half)}`;
  const r = (half * half + h * h) / (2 * h);
  return `M${n(x - half)} ${n(y + lift / 2)}A${n(r)} ${n(r)} 0 0 ${lift > 0 ? 1 : 0} ${n(x + half)} ${n(y + lift / 2)}`;
}

const crossEye = (x, y, s) => `M${n(x - s)} ${n(y - s)}L${n(x + s)} ${n(y + s)}M${n(x + s)} ${n(y - s)}L${n(x - s)} ${n(y + s)}`;

// A pill whose top is cut on a slant, outer corner lower: a drooping lid.
function lidEye(cx, cy, w, h, cut, side) {
  const r = w / 2, top = cy - h / 2 + r * 0.5, low = cy + h / 2 - r;
  const outer = cx + side * r, inner = cx - side * r;
  return `M${n(inner)} ${n(top)}L${n(outer)} ${n(top + cut)}V${n(low)}A${n(r)} ${n(r)} 0 0 ${side > 0 ? 1 : 0} ${n(inner)} ${n(low)}Z`;
}

// A heavy-lidded eye: the part of an upright pill below a flat lid that
// covers `cut` of it, squeezed up toward the lid as the eye closes (open 1 to
// 0). Arcs go elliptical as it squeezes; SvgCanvas draws them.
function heavyEye(cx, cy, w, h, cut, open) {
  const r = w / 2, y0 = cy - h / 2, y1 = cy + h / 2, yc = Math.min(y0 + h * cut, y1 - r);
  const Y = y => yc + (y - yc) * open, ry = r * open;
  if (yc >= y0 + r) return `M${n(cx - r)} ${n(yc)}H${n(cx + r)}V${n(Y(y1 - r))}A${n(r)} ${n(ry)} 0 0 1 ${n(cx - r)} ${n(Y(y1 - r))}Z`;
  const a = Math.sqrt(Math.max(0, r * r - (y0 + r - yc) ** 2));
  return `M${n(cx - a)} ${n(yc)}H${n(cx + a)}A${n(r)} ${n(ry)} 0 0 1 ${n(cx + r)} ${n(Y(y0 + r))}V${n(Y(y1 - r))}`
    + `A${n(r)} ${n(ry)} 0 0 1 ${n(cx - r)} ${n(Y(y1 - r))}V${n(Y(y0 + r))}A${n(r)} ${n(ry)} 0 0 1 ${n(cx - a)} ${n(yc)}Z`;
}

function face(o, S, eye) {
  const F = S.face, sn = Math.sin(rad(o.spin));
  const size = F.es * (1 + 0.16 * o.wide), ew = EYE_W * size, eh = EYE_H * size, sw = 1.5 * F.es;
  const shift = o.turn * 3.2 + sn * 7;
  const cx = F.x + o.look[0] * 2.2 + shift, cy = F.y + o.look[1] * 1.8;
  const shut = Math.max(o.happy, o.sleep, o.cross);
  const open = o.open * (1 - 0.55 * o.squint) * clamp(1 - 2 * shut);
  let fills = '', lines = '';
  for (const side of [-1, 1]) {
    const x = cx + side * F.gap / 2 * (1 - 0.12 * Math.abs(o.turn));
    const narrow = side * o.turn > 0 ? 1 - 0.25 * Math.abs(o.turn) : 1;
    if (o.cross >= 0.5) lines += crossEye(x, cy, ew * 0.46 * (2 * o.cross - 1));
    else if (o.sleep >= 0.5) lines += arcEye(x, cy + eh * 0.12, ew * 1.15 * narrow, -ew * 0.42 * (2 * o.sleep - 1));
    else if (o.happy >= 0.5 || (side > 0 && o.wink >= 0.5)) lines += arcEye(x, cy - eh * 0.04, ew * 1.15 * narrow, ew * 0.55 * (2 * Math.max(o.happy, o.wink) - 1));
    else if (o.lid > 0.02) {
      // Resting: heavy lids, half way down at lid 1; a blink shuts them to a
      // flat line, never the curve sleeping uses.
      const w = ew * narrow, cut = 0.5 * o.lid;
      if (eh * (1 - cut) * open < 0.6) lines += `M${n(x - w * 0.5)} ${n(cy - eh / 2 + eh * cut)}H${n(x + w * 0.5)}`;
      else fills += heavyEye(x, cy, w, eh, cut, open);
    } else {
      const h = Math.max(ew * 0.42, eh * open), w = (h < ew ? ew * 1.08 : ew) * narrow;
      fills += o.sad > 0.05 && h > ew * 1.4 ? lidEye(x, cy, w, h, o.sad * h * 0.28, side) : pill(x, cy, w, h);
    }
  }
  // The mouth: an open oval, or a grin (flat top, round bottom) when happy.
  if (o.mouth > 0.04 && o.cross < 0.5) {
    const mx = F.x + o.look[0] + shift;
    if (o.happy >= 0.5) {
      const r = 1.5 + 0.9 * o.mouth, top = Math.min(F.y + 5.4, 12.8 - r);
      fills += `M${n(mx - r)} ${n(top)}A${n(r)} ${n(r)} 0 0 0 ${n(mx + r)} ${n(top)}Z`;
    } else {
      const mw = 2.2 + 1.8 * o.mouth, mh = 0.9 + 3.4 * o.mouth;
      fills += pill(mx, Math.min(F.y + 6.2 + 0.3 * mh, 12.8 - mh / 2), mw, mh);
    }
  }
  return P(fills, fill(eye)) + P(lines, stroke(eye, sw));
}

function propLayer(props, c, layer) {
  let out = '';
  for (const name in props) {
    const def = PROPS[name], p = props[name], a = p.a ?? 1;
    if (!def || a < 0.02) continue;
    const s = def.layer === 'split' ? drawMinis(p, c, layer === 'back') : def.layer === layer ? def.draw(p, c) : '';
    if (s) out += a < 0.995 ? `<g${O(a)}>${s}</g>` : s;
  }
  return out;
}

function draw(o, { id, rim, shape, color, size }) {
  const S = SHAPES[shape], col = COLOR_HEX[color], c = { col, eye: eyeOn(col), id };
  const cs = Math.cos(rad(o.spin)), back = cs < 0;
  const wk = Math.max(0.06, Math.abs(cs)) * (1 - 0.07 * Math.abs(o.turn));
  // A hop eases off as the head nears the edge of the circle; stretching
  // taller uses up some of the room.
  const room = Math.max(0.5, S.room - Math.max(0, o.sy - 1) * (15 - S.top));
  const y = o.y < 0 ? -room * Math.tanh(-o.y / room) : o.y;
  const r = rad(o.rot), co = Math.cos(r), si = Math.sin(r), sx = o.sx * wk, sy = o.sy;
  const tf = matrix(co * sx, si * sx, -si * sy, co * sy, BASE_X + o.x + 15 * si * sy, S.base + y - 15 * co * sy);
  const body = (rim ? `<use href="#${id}s" fill="none" stroke="${PAPER}" stroke-width="2.6" stroke-linejoin="round"/>` : '')
    + `<use href="#${id}s"${fill(back ? mixHex(col, INK, 0.22) : col)}/>`
    + (cs > 0.35 ? face(o, S, c.eye) : '');
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 44 44" width="${n(size)}" height="${n(size)}">`
    + `<defs><path id="${id}s" d="${S.d}"/></defs>`
    + propLayer(o.props, c, 'back') + `<g${tf}>${body}</g>` + propLayer(o.props, c, 'front') + '</svg>';
}
