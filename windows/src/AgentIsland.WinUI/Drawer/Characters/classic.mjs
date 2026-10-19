// Agent Island - Classic desk companions
//
// The Classic style's characters: six desk companions of our own. A potted
// succulent whose rosette echoes Claude's starburst, a little desk computer
// with >_ in its screen (Codex), a violet flying saucer (Antigravity), a
// ringed planet ornament (Grok, and Cursor), an astronaut helmet with a >_
// collar (Kimi Code) and a deep-sea submarine with a whale's tail
// (DeepSeek). Each acts out its state with its eyes, its posture and its own
// mechanism; the only badges are "?" (asking) and "!" (approving), popped up
// over the head like an emote in the companion's own colour.
//
// Pure JS, with no imports, DOM, timers or platform randomness: every random
// choice comes from the bot's seed, so the same seed and calls always draw
// the same frames. Frames stay inside the drawer's SvgCanvas subset: svg,
// defs, g, path (M L H V C A Z), circle and use; transform, opacity, fill,
// stroke, stroke-width, stroke-linecap and stroke-linejoin; #RRGGBB colours.

export const KINDS = ['claude', 'codex', 'antigravity', 'grok', 'cursor', 'kimi', 'deepseek'];

// The shared state names (the Pixel set's), plus resting.
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
const RIM = 2.6;

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
// A damped spring kick: 0 at rest, positive first.
const kick = (q, freq = 14, decay = 6) => (q < 0 || q > 1.4) ? 0 : Math.sin(q * freq) * Math.exp(-q * decay);
// A back-out curve, 0 to 1 with a small overshoot.
const backOut = (t, s = 1.7) => { t = clamp(t) - 1; return t * t * ((s + 1) * t + s) + 1; };
// Keeps v within +-cap, easing into the limit instead of stopping at it.
const soft = (v, cap) => cap * Math.tanh(v / cap);

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
const P = (d, attrs) => (d ? `<path d="${d}"${attrs}/>` : '');
const F = c => ` fill="${c}"`;
// A stroke; a round join only where the line turns a corner.
// A white sticker rim for a fill.
const RIMF = ` fill="${PAPER}" stroke="${PAPER}" stroke-width="${RIM}" stroke-linejoin="round"`;
const S = (c, w, cap = 'round', join = false) => ` fill="none" stroke="${c}" stroke-width="${n(w)}" stroke-linecap="${cap ?? 'round'}"${join ? ' stroke-linejoin="round"' : ''}`;

const hexRgb = h => [parseInt(h.slice(1, 3), 16), parseInt(h.slice(3, 5), 16), parseInt(h.slice(5, 7), 16)];
const rgbHex = rgb => '#' + rgb.map(v => Math.round(clamp(v, 0, 255)).toString(16).padStart(2, '0')).join('').toUpperCase();
const mixHex = (a, b, t) => (t <= 0.002 ? a : t >= 0.998 ? b : rgbHex(hexRgb(a).map((v, i) => lerp(v, hexRgb(b)[i], t))));

// A path builder that tracks the pen; it writes only absolute commands.
function pen(x0, y0) {
  let d = `M${n(x0)} ${n(y0)}`;
  const api = {
    M(x, y) { d += `M${n(x)} ${n(y)}`; return api; },
    L(x, y) { d += `L${n(x)} ${n(y)}`; return api; },
    C(x1, y1, x2, y2, x, y) { d += `C${n(x1)} ${n(y1)} ${n(x2)} ${n(y2)} ${n(x)} ${n(y)}`; return api; },
    A(rx, ry, large, sweep, x, y) { d += `A${n(rx)} ${n(ry)} 0 ${large} ${sweep} ${n(x)} ${n(y)}`; return api; },
    Z() { d += 'Z'; return api; },
    get d() { return d; },
  };
  return api;
}
// A rounded rectangle by its top-left corner, and an ellipse by its centre.
const rrect = (x, y, w, h, r) => `M${n(x + r)} ${n(y)}H${n(x + w - r)}A${n(r)} ${n(r)} 0 0 1 ${n(x + w)} ${n(y + r)}V${n(y + h - r)}`
  + `A${n(r)} ${n(r)} 0 0 1 ${n(x + w - r)} ${n(y + h)}H${n(x + r)}A${n(r)} ${n(r)} 0 0 1 ${n(x)} ${n(y + h - r)}V${n(y + r)}A${n(r)} ${n(r)} 0 0 1 ${n(x + r)} ${n(y)}Z`;
const ell = (cx, cy, rx, ry) => pen(cx - rx, cy).A(rx, ry, 0, 1, cx + rx, cy).A(rx, ry, 0, 1, cx - rx, cy).Z().d;
const circ = (cx, cy, r) => ell(cx, cy, r, r);
// An upright pill (an oval with straight sides), or a lying one when short.
function pill(cx, cy, w, h) {
  const r = Math.min(w, h) / 2, x0 = cx - w / 2, x1 = cx + w / 2, y0 = cy - h / 2, y1 = cy + h / 2;
  if (h <= w) return `M${n(x0 + r)} ${n(y0)}H${n(x1 - r)}A${n(r)} ${n(r)} 0 0 1 ${n(x1 - r)} ${n(y1)}H${n(x0 + r)}A${n(r)} ${n(r)} 0 0 1 ${n(x0 + r)} ${n(y0)}Z`;
  return `M${n(x0)} ${n(y0 + r)}A${n(r)} ${n(r)} 0 0 1 ${n(x1)} ${n(y0 + r)}V${n(y1 - r)}A${n(r)} ${n(r)} 0 0 1 ${n(x0)} ${n(y1 - r)}Z`;
}
// An arc of a circle round (cx, cy), clockwise from a0 to a1 degrees.
function arcD(cx, cy, r, a0, a1) {
  const p = a => [cx + r * Math.cos(rad(a)), cy + r * Math.sin(rad(a))];
  const [x0, y0] = p(a0), [x1, y1] = p(a1);
  return `M${n(x0)} ${n(y0)}A${n(r)} ${n(r)} 0 ${a1 - a0 > 180 ? 1 : 0} 1 ${n(x1)} ${n(y1)}`;
}
const poly = pts => pts.map(([x, y], i) => `${i ? 'L' : 'M'}${n(x)} ${n(y)}`).join('') + 'Z';
// Point (x, y) turned by deg degrees about (cx, cy).
const turnPt = (x, y, cx, cy, deg) => {
  const c = Math.cos(rad(deg)), s = Math.sin(rad(deg));
  return [cx + (x - cx) * c - (y - cy) * s, cy + (x - cx) * s + (y - cy) * c];
};

// A body: parts back to front, each under the same white rim, so the object
// reads on any wallpaper. A part is a fill ({ d, fill }), a stroke ({ d,
// line: [colour, width], cap, join }) or a disc ({ c: [x, y, r], fill });
// `edge` rounds a fill with a stroke of its own colour; `bare` parts sit
// inside another and take no rim; `a` fades a part; `use` draws a defs path
// instead of d. Rims of one width share a path, and so do neighbouring
// fills of one colour, to keep frames small.
function body(parts, rim) {
  const rims = new Map();
  let extra = '', fills = '', run = null;
  const flush = () => { if (run) fills += `<path d="${run.d}"${F(run.fill)}/>`; run = null; };
  for (const p of parts) {
    const a = p.a ?? 1;
    if (a < 0.02) continue;
    const fade = O(a);
    if (rim && !p.bare) {
      if (p.c) extra += `<circle cx="${n(p.c[0])}" cy="${n(p.c[1])}" r="${n(p.c[2] + RIM / 2)}"${F(PAPER)}${fade}/>`;
      else if (p.use || a < 0.995) {
        const geo = p.use ? `<use href="#${p.use}"` : `<path d="${p.d}"`;
        extra += p.line ? `${geo}${S(PAPER, p.line[1] + RIM, p.cap, p.join)}${fade}/>`
          : `${geo}${F(PAPER)} stroke="${PAPER}" stroke-width="${n(RIM + (p.edge ?? 0))}" stroke-linejoin="round"${fade}/>`;
      } else {
        const key = p.line ? `l${p.line[1] + RIM}|${p.cap ?? 'round'}|${p.join ? 1 : 0}` : `f${RIM + (p.edge ?? 0)}`;
        rims.set(key, (rims.get(key) ?? '') + p.d);
      }
    }
    if (!p.c && !p.use && !p.line && !p.edge && a >= 0.995) {
      if (run && run.fill === p.fill) run.d += p.d;
      else { flush(); run = { d: p.d, fill: p.fill }; }
      continue;
    }
    flush();
    if (p.c) fills += `<circle cx="${n(p.c[0])}" cy="${n(p.c[1])}" r="${n(p.c[2])}"${F(p.fill)}${fade}/>`;
    else {
      const geo = p.use ? `<use href="#${p.use}"` : `<path d="${p.d}"`;
      fills += p.line ? `${geo}${S(p.line[0], p.line[1], p.cap, p.join)}${fade}/>`
        : `${geo}${F(p.fill)}${p.edge ? ` stroke="${p.fill}" stroke-width="${n(p.edge)}" stroke-linejoin="round"` : ''}${fade}/>`;
    }
  }
  flush();
  let out = '';
  for (const [key, d] of rims) {
    if (key[0] === 'f') out += `<path d="${d}"${F(PAPER)} stroke="${PAPER}" stroke-width="${n(+key.slice(1))}" stroke-linejoin="round"/>`;
    else { const [w, cap, join] = key.slice(1).split('|'); out += `<path d="${d}"${S(PAPER, +w, cap, join === '1')}/>`; }
  }
  return out + extra + fills;
}

// MARK: - Poses

// Channels every pose carries; numbers blend across a state change.
//   body:      x, y (down), rot (deg), sx, sy
//   eyes:      look (pair offset, -1 to 1), gazeW (how far the pointer
//              steers it), k (capsule height), ka (unevenness), wide, open
//              (blinks), blink (1 = random blinks allowed), happy, sleep,
//              rest (closed: arches, flat, soft arcs down), worry
//   mechanism: each companion reads these its own way (see 30-objects.js):
//              turn (deg), out (output, 0 to 1; below 0 rewinds), marks
//              (output marks, 0 to 3), send (outgoing, 0 to 1), beat,
//              press, float (DIP, up), aim (-1 to 1), alt (-1 to 1),
//              spread (opens out; below 0 closes in), bend (fault), slack
//              (paused), hold (held still), play (a fidget's toy), orb
//              (a turn round its orbit, as [cos, sin], so it blends
//              smoothly)
//   badges:    ask ("?"), alert ("!"; above 1 it pulses), bob (-1 to 1,
//              how the badge moves its own way)
const REST = {
  x: 0, y: 0, rot: 0, sx: 1, sy: 1,
  look: [0, 0], gazeW: 1, k: 1, ka: 0, wide: 0, open: 1, blink: 1, happy: 0, sleep: 0, rest: 0, worry: 0,
  turn: 0, out: 0, marks: 0, send: 0, beat: 0, press: 0, float: 0, aim: 0, alt: 0, spread: 0, bend: 0, slack: 0, hold: 0, play: 0, orb: [1, 0],
  ask: 0, alert: 0, bob: 0,
};

function mixPoses(a, b, w) {
  const out = {};
  for (const key in b) {
    const va = a[key], vb = b[key];
    if (Array.isArray(vb) && Array.isArray(va)) out[key] = vb.map((v, i) => lerp(va[i], v, w));
    else out[key] = lerp(va, vb, w);
  }
  return out;
}

// MARK: - Bots

export function createBot(kind, { seed = 1, tempo = 1, rim = true } = {}) {
  const def = KIND[kind] ?? KIND.claude;
  const obj = OBJ[def.object];
  const random = rng(seed);
  const k = { kind, seed, obj, r: Array.from({ length: 24 }, random) };
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

  const poseOf = s => ({ ...REST, ...stateOf(s.state)(s.t, k) });
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
      // A click: a quick hop with a squash on take-off and landing, happy
      // eyes, and a resting bot's eyes stay open a moment before closing.
      const p = T - pokeAt;
      const hop = p >= 0 && p < 0.55 ? bump(p / 0.42) : 0;
      const squash = p >= 0 && p < 0.55 ? kick(p, 22, 8) * 0.05 : 0;
      const stir = p >= 0 && p < 2.6 ? smooth(p / 0.15) * (1 - smooth((p - 1.2) / 1.4)) : 0;
      const live = {
        ...pose,
        y: pose.y - hop * obj.hop,
        float: pose.float + hop * obj.hopFloat,
        sx: pose.sx * (1 + squash),
        sy: pose.sy * (1 - squash),
        rot: pose.rot + lean * obj.tilt * 0.5,
        look: [eyes[0], eyes[1]],
        open: pose.open * (1 - blink),
        rest: pose.rest * (1 - stir),
        happy: Math.max(pose.happy, p >= 0 && p < 0.5 ? bump(p / 0.5) : 0),
      };
      return draw(live, { id, rim: rimOn, obj, size });
    },
  };
  pose = blended();
  return bot;
}

export const fidgetsOf = kind => (KIND[kind] ?? KIND.claude).fidgets;
export const settleOf = state => (SETTLE[state] ? SETTLE[state][0] : null);
export const swatchOf = kind => parseInt((KIND[kind] ?? KIND.claude).swatch.slice(1), 16);
export const cycleOf = state => CYCLE[state] ?? 4;

// MARK: - Eyes and badges
//
// Codex's eye rules: an open eye is one solid capsule, and the pair moves
// together for gaze; concentration shortens the whole capsule, never cuts
// it. Closed eyes are strokes: arches when happy, flat when asleep, soft
// arcs down when resting. Worry shrinks the pair, unevenly.

// A closed eye: an arc of width w through (x, y), bulging up (lift > 0) or
// down (lift < 0), flat in between.
function arcEye(x, y, w, lift) {
  const half = w / 2, h = Math.abs(lift);
  if (h < 0.05) return `M${n(x - half)} ${n(y)}H${n(x + half)}`;
  const r = (half * half + h * h) / (2 * h);
  return `M${n(x - half)} ${n(y + lift / 2)}A${n(r)} ${n(r)} 0 0 ${lift > 0 ? 1 : 0} ${n(x + half)} ${n(y + lift / 2)}`;
}

// E: the object's eye spec { x, y, gap, w, h, travel } at rest.
function eyePair(o, E, col) {
  const cx = E.x + o.look[0] * E.travel, cy = E.y + o.look[1] * E.travel;
  const size = 1 + 0.12 * o.wide;
  const w = E.w * size, h = E.h * size, sw = Math.max(1.1, E.w * 0.42);
  const shut = Math.max(o.happy, o.sleep, o.rest);
  const open = o.open * clamp(1 - 2 * shut);
  let fills = '', lines = '';
  for (const side of [-1, 1]) {
    const x = cx + side * E.gap / 2;
    if (o.happy >= 0.5) lines += arcEye(x, cy + h * 0.06, w * 1.3, w * 0.62 * (2 * o.happy - 1));
    else if (o.sleep >= 0.5) lines += arcEye(x, cy + h * 0.2, w * 1.3, 0);
    else if (o.rest >= 0.5) lines += arcEye(x, cy + h * 0.1, w * 1.3, -w * 0.5 * (2 * o.rest - 1));
    else {
      // Worry: smaller and uneven, the right eye a little lower.
      const k = o.k * (1 + side * o.ka) * (1 - o.worry * (side > 0 ? 0.46 : 0.36));
      const hh = Math.max(w * 0.46, h * k * open), ww = hh < w ? w * 1.06 : w;
      fills += pill(x, cy + o.worry * (side > 0 ? 0.7 : 0.4), ww, hh);
    }
  }
  return P(fills, F(col)) + P(lines, S(col, sw));
}

// The only badges: "?" while asking and "!" while approving, popped up
// from the companion like an emote: the glyph itself as a fat sticker in the
// companion's main colour under the same white rim as its body. It springs
// up out of the head with an overshoot as it appears, sways while asking
// and pulses while approving. obj.emote = { at, color }.
const EMOTE = { s: 1.55, w: 2.5 };
// Where an emote can sit: pulled in toward the centre until the glyph, at
// its biggest pop, stays inside the drawer's circle.
const emoteAt = ([x, y]) => {
  const d = Math.hypot(x - 22, y - 22), lim = 15;
  return d > lim ? [22 + (x - 22) * lim / d, 22 + (y - 22) * lim / d] : [x, y];
};
function badge(o, obj) {
  const q = o.ask >= o.alert, v = q ? o.ask : o.alert, a = clamp(v);
  if (a < 0.02) return '';
  const { s: k, w } = EMOTE, e = obj.emote, [x, y] = e.at;
  const s = backOut(a, 2.4) * Math.max(1, v), r = rad(q ? -8 + 10 * o.bob : -4);
  const c = Math.cos(r) * s, si = Math.sin(r) * s;
  const X = v => n(v * k);
  const line = q ? `M${X(-1.05)} ${X(-1)}C${X(-1.05)} ${X(-2.4)} ${X(1.15)} ${X(-2.45)} ${X(1.15)} ${X(-1.05)}C${X(1.15)} ${X(-0.15)} 0 ${X(-0.2)} 0 ${X(0.55)}`
    : `M0 ${X(-2)}V${X(0.5)}`;
  const dot = (rr, col) => `<circle cx="0" cy="${X(1.85)}" r="${n(rr)}"${F(col)}/>`;
  return `<g${matrix(c, si, -si, c, x, y + 2.6 * (1 - a))}>` + P(line, S(PAPER, w + RIM)) + dot(w * 0.7 + RIM / 2, PAPER)
    + P(line, S(e.color, w)) + dot(w * 0.7, e.color) + '</g>';
}

// MARK: - States
//
// Each state is a pose over time: the channels in REST. A looping state
// works in u, its place in the cycle (0 to 1), and every wave in it turns a
// whole number of times per cycle, so the loop meets itself. A one-shot
// state works in seconds from its start. k carries the bot's seeded randoms
// and its object (k.obj.mech: where its mechanism is from the eyes;
// k.obj.outDir: where its output goes).
//
// After Codex's state tables (expression-refinement/characters/*.md): no
// floating symbols but the two badges; meaning comes from the eyes, the
// posture and each companion's own parts.

const CYCLE = {
  idle: 12, thinking: 2.4, streaming: 0.9, reading: 3.2, searching: 3.6, web: 2.4, writing: 1.6,
  running: 0.6, controlling: 2.8, spawning: 2.6, orbit: 3.2, sending: 1.8, asking: 2.4,
  approving: 1.6, compacting: 1.4, working: 2.4, listening: 4.8, broken: 5, offline: 3.6,
  sleeping: 4.2, locked: 3.2, resting: 8,
  fiddle: 2.8, peek: 2.6, hop: 1.4, sway: 2,
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

const LOOP = {
  // Breathes, sways a little and glances about, two glances a cycle; the
  // mechanism drifts with the breath.
  idle(u, k) {
    const [r0, r1, r2, r3, r4, r5] = k.r;
    const shift = r5 * 0.08;
    const look = u < 0.16 + shift ? [0, 0]
      : u < 0.36 + shift ? [(r1 - 0.5) * 1.4, (r2 - 0.5) * 0.7]
        : u < 0.56 ? [0, 0]
          : u < 0.78 ? [(r3 - 0.5) * 1.4, -0.15 - r4 * 0.3]
            : [0, 0];
    return {
      ...breathe(Math.sin(TAU * u * 4), 0.015), rot: 1.2 * Math.sin(TAU * (u + r0)), look,
      turn: 3 * Math.sin(TAU * (u * 2 + r1)), float: 0.35 * Math.sin(TAU * u * 3),
    };
  },
  // Waiting for your reply: relaxed, eyes on you, a slow tilt.
  listening(u) {
    return { ...breathe(Math.sin(TAU * u * 2), 0.014), rot: 1.6 * S1(u), look: [0, -0.05], wide: 0.08, float: 0.25 + 0.15 * Math.sin(TAU * u * 2) };
  },
  // Tilted, the pair up and right, one eye a little shorter; the mechanism
  // rests off its usual place.
  thinking(u) {
    return {
      rot: -3 + 1.2 * S1(u), y: -0.4 + 0.4 * Math.cos(TAU * u * 2),
      look: [0.45 + 0.12 * S1(u), -0.75], k: 0.86, ka: 0.08, gazeW: 0.25,
      turn: 6 + 3 * S1(u), aim: 0.3, float: 0.7 + 0.2 * Math.cos(TAU * u * 2),
    };
  },
  // Replying: three beats a cycle, a mark of output on each, then they clear.
  streaming(u) {
    const s = 0.5 - 0.5 * Math.cos(TAU * u * 3);
    const i = Math.min(2, Math.floor(u * 3));
    const marks = (i + smooth(seg(frac(u * 3), 0.05, 0.35))) * (1 - smooth(seg(u, 0.86, 1)));
    return {
      y: -0.45 * s, sy: 1 + 0.02 * s, sx: 1 - 0.012 * s,
      look: [0.25, 0.1], k: 1.08, gazeW: 0.6, beat: s, marks, out: marks / 3, turn: 5 * s,
    };
  },
  // Two lines a cycle: the pair sweeps across low, then flicks back.
  reading(u) {
    const line = frac(u * 2), scan = line < 0.8 ? easeInOut(line / 0.8) : 1 - easeInOut((line - 0.8) / 0.2);
    return {
      look: [lerp(-0.6, 0.6, scan), 0.72], k: 0.82, rot: 2 + lerp(-1, 1, scan), gazeW: 0.15,
      aim: lerp(-0.4, 0.4, scan), float: -0.3,
    };
  },
  // Three deliberate stops a cycle, eyes tall, the mechanism turned to each
  // bearing.
  searching(u) {
    const stops = [-1, 1, 0.1], p = u * 3, i = Math.min(2, Math.floor(p)), f = p - i;
    const a = lerp(stops[(i + 2) % 3], stops[i], easeInOut(seg(f, 0, 0.45)));
    return { look: [0.75 * a, -0.1], k: 1.12, wide: 0.15, rot: 2 * a, aim: a, turn: 12 * a, gazeW: 0.2 };
  },
  // A broader, slower sweep, looking up.
  web(u) {
    const a = S1(u), b = Math.sin(TAU * u * 2);
    return { look: [0.6 * a, -0.5], k: 1.1, wide: 0.2, rot: 1.5 * a, aim: 0.8 * a, turn: 22 * a, float: 0.6 + 0.2 * b, gazeW: 0.3 };
  },
  // Output advances in three short steps, eyes short and low, then clears.
  writing(u) {
    const p = seg(u, 0, 0.78) * 3;
    const out = Math.min(1, (Math.floor(p) + smooth(seg(frac(p), 0, 0.5))) / 3) * (1 - smooth(seg(u, 0.84, 1)));
    return { k: 0.72, look: [0.2 + 0.3 * out, 0.62], rot: 2.5, out, marks: 3 * out, turn: 12 * out, gazeW: 0.15 };
  },
  // Firm and fast: two command beats a cycle, the mechanism engaged.
  running(u) {
    const h = 0.5 - 0.5 * Math.cos(TAU * u * 2);
    return {
      rot: 3, y: -0.3 * h, sy: 1 - 0.02 * h, sx: 1 + 0.014 * h,
      k: 0.7, look: [0.35, 0.3], gazeW: 0.2, press: h, beat: h, turn: 18 * S1(u),
    };
  },
  // The pair jumps between three targets, with a press at two of them.
  controlling(u) {
    const A = [0.7, -0.1], B = [0.55, -0.8], C = [-0.6, 0.6];
    const go = (p, q, s) => [lerp(p[0], q[0], s), lerp(p[1], q[1], s)];
    let pt = A, click = 0;
    if (u < 0.08) pt = A;
    else if (u < 0.32) pt = go(A, B, easeInOut(seg(u, 0.08, 0.32)));
    else if (u < 0.45) { pt = B; click = bump(seg(u, 0.34, 0.44)); }
    else if (u < 0.68) pt = go(B, C, easeInOut(seg(u, 0.45, 0.68)));
    else if (u < 0.8) { pt = C; click = bump(seg(u, 0.69, 0.79)); }
    else pt = go(C, A, easeInOut(seg(u, 0.8, 1)));
    return { look: pt, k: 0.95, press: click, aim: pt[0], turn: 8 * pt[0], rot: 1.5 * pt[0], gazeW: 0.15 };
  },
  // The mechanism opens out, eyes wide on it, then closes again.
  spawning(u, k) {
    const pre = bump(seg(u, 0, 0.2)), pop = seg(u, 0.18, 0.4), gone = seg(u, 0.8, 1);
    const s = backOut(pop, 2) * (1 - easeIn(gone));
    const m = k.obj.mech, g = 0.3 + 0.6 * clamp(s);
    return {
      sy: 1 - 0.05 * pre + 0.03 * bump(pop), sx: 1 + 0.035 * pre - 0.015 * bump(pop),
      k: 1.1, wide: 0.25 * clamp(s), look: [m[0] * g, m[1] * g], spread: s, gazeW: 0.2,
    };
  },
  // Waiting on helpers: the mechanism changes bearing slowly, goes once
  // round its orbit, and the eyes go from side to side.
  orbit(u) {
    const alt = S1(u);
    return { ...breathe(Math.cos(TAU * u * 2), 0.012), alt, orb: [Math.cos(TAU * u), Math.sin(TAU * u)], look: [0.7 * alt, 0.3], aim: 0.6 * alt, turn: 20 * alt, gazeW: 0.2 };
  },
  // Winds back, pushes its output out and watches it go.
  sending(u, k) {
    const back = smooth(seg(u, 0.06, 0.26)) * (1 - smooth(seg(u, 0.26, 0.34)));
    const fwd = smooth(seg(u, 0.26, 0.34)) * (1 - smooth(seg(u, 0.42, 0.75)));
    const send = smooth(seg(u, 0.28, 0.55)) * (1 - smooth(seg(u, 0.8, 1)));
    const d = k.obj.outDir, g = 0.2 + 0.6 * send;
    return {
      rot: -3 * back + 3 * fwd, x: -0.6 * back + 0.6 * fwd, sy: 1 - 0.03 * back + 0.02 * fwd,
      look: [d[0] * g, d[1] * g], wide: 0.15 * fwd, press: fwd, send, gazeW: 0.2,
    };
  },
  // Head tilted, eyes tall and up, and the "?" badge.
  asking(u) {
    return {
      rot: 5 + 1.5 * S1(u), y: -0.4 + 0.4 * Math.cos(TAU * u * 2),
      look: [0.25, -0.4], k: 1.14, ka: 0.06, wide: 0.15, gazeW: 0.7,
      ask: 1, bob: S1(u), float: 0.7, turn: -6,
    };
  },
  // Upright and still, eyes on you, the "!" badge pulsing twice a cycle.
  approving(u) {
    return { look: [0, 0.02], k: 1.08, gazeW: 1, alert: 1 + 0.12 * bump(frac(u * 2)), hold: 0.6 };
  },
  // Squeezed in, eyes short bars, the output wound back; then it springs back.
  compacting(u) {
    const sq = easeInOut(seg(u, 0, 0.45)) * (1 - easeOut(seg(u, 0.55, 0.7)));
    const wob = settle(u, 0.62, CYCLE.compacting);
    return { sx: 1 - 0.05 * sq + 0.02 * wob, sy: 1 + 0.03 * sq - 0.015 * wob, k: 1 - 0.5 * sq, out: -0.6 * sq, spread: -sq, gazeW: 0.3 };
  },
  // Leaning in, focused, the mechanism working back and forth.
  working(u) {
    const s = Math.sin(TAU * u * 2);
    return { rot: 2 + 1.5 * s, y: -0.5 * Math.abs(s), k: 0.85, look: [0.3, -0.35], aim: 0.5 * s, turn: 8 * s, out: 0.4 + 0.3 * s, gazeW: 0.3 };
  },
  // Slumped, eyes small and uneven, the mechanism stuck askew, a twitch now
  // and then.
  broken(u) {
    const twitch = settle(u, 0.62, CYCLE.broken, 20, 7);
    return { sy: 0.96 + 0.01 * S1(u), sx: 1.03, rot: -4 + S1(u) + 1.5 * twitch, y: 0.5, worry: 1, look: [0, 0.3], bend: 1, gazeW: 0 };
  },
  // Lowered and quiet, eyes short and patient, the output paused.
  offline(u) {
    return { y: 0.5, rot: -1.5 + S1(u), sy: 0.98 + 0.01 * Math.sin(TAU * u * 2), k: 0.72, look: [-0.1, 0.42], slack: 1, float: -0.4, gazeW: 0.3 };
  },
  // Out of quota: slumped low, eyes shut flat, deep breaths.
  sleeping(u) {
    return { ...breathe(S1(u), 0.03), rot: -4, y: 0.9, sleep: 1, blink: 0, slack: 1, float: -0.7, gazeW: 0 };
  },
  // Sign-in needed: its mechanism held shut; it tries once a cycle and can't.
  locked(u) {
    const strain = kick(u * CYCLE.locked, 18, 5);
    return { look: [0, 0.05], k: 1, hold: 1, press: 1, rot: 0.8 * strain, turn: 3 * strain, gazeW: 0.6 };
  },
  // Its app is closed or out of sight: settled, eyes closed in soft arcs,
  // two slow breaths, and once a cycle a sleepy peek.
  resting(u, k) {
    const peek = bump(seg(u, 0.56, 0.7)), b = Math.sin(TAU * u * 2);
    return {
      y: 0.4, sy: 0.97 + 0.02 * b, sx: 1.02 - 0.012 * b, rot: 0.6 * Math.sin(TAU * (u + k.r[0])),
      rest: 1 - peek, k: 0.62, blink: 0, look: [0, 0.3], slack: 0.5, float: -0.3, gazeW: 0.4,
    };
  },

  // Fidgets: short idle habits the drawer starts now and then. Each is play:
  // happy or curious eyes, never a badge, a work rhythm or closed eyes.
  // Plays with its own toy, eyes smiling: the rosette fans, the computer's
  // screen brightens as it bobs, the saucer's lights spin round, the moon
  // zips round the ring, the helmet floats up, the submarine surfaces in a
  // burst of bubbles.
  fiddle(u) {
    const play = Math.pow(Math.sin(PI * u), 2);
    return { play, happy: 0.9 * smooth(seg(u, 0.08, 0.22)) * (1 - smooth(seg(u, 0.78, 0.92))), y: -0.5 * play, gazeW: 0.3 };
  },
  // Leans out to one side, then the other, eyes wide.
  peek(u) {
    const l = smooth(seg(u, 0.05, 0.2)) * (1 - smooth(seg(u, 0.42, 0.55)));
    const r = smooth(seg(u, 0.55, 0.7)) * (1 - smooth(seg(u, 0.88, 1)));
    return { x: 0.8 * (r - l), rot: 4 * (r - l), look: [0.9 * (r - l), -0.25 * (l + r)], wide: 0.3 * (l + r), aim: r - l, gazeW: 0.2 };
  },
  // Two happy hops.
  hop(u) {
    const h = hops(u, 0.08, 0.72, [1.5, 0.9]);
    const w = settle(u, 0.72, CYCLE.hop, 18, 7);
    return { y: h.y, sy: 1 - 0.05 * h.c - 0.02 * w, sx: 1 + 0.035 * h.c + 0.015 * w, happy: 0.85 };
  },
  // A little side-step dance, eyes happy.
  sway(u) {
    const s = Math.sin(TAU * u), b = Math.abs(Math.sin(TAU * u * 2));
    return { x: 1.2 * s, rot: -4 * s, y: -0.6 * b, ...breathe(-b, 0.025), happy: 0.9, gazeW: 0.3 };
  },
};

const ONCE = {
  // A crouch, one lift with a smile, a squashy landing; parts go home.
  finished(t) {
    const crouch = smooth(seg(t, 0, 0.16)) * (1 - smooth(seg(t, 0.16, 0.22)));
    const q = seg(t, 0.18, 0.62);
    const land = t < 0.62 ? 0 : t < 0.66 ? smooth((t - 0.62) / 0.04) : Math.exp(-(t - 0.66) * 14);
    const wob = t > 0.66 ? kick(t - 0.66, 16, 6) : 0;
    return {
      y: arc(q, 1.8), sy: 1 - 0.08 * crouch - 0.08 * land - 0.02 * wob, sx: 1 + 0.05 * crouch + 0.05 * land + 0.015 * wob,
      happy: smooth(seg(t, 0.16, 0.3)), float: 1.2 * bump(seg(t, 0.15, 0.9)),
      look: [0, -0.2 * bump(q)], gazeW: 0.5,
    };
  },
  // A startle, a shudder, then it slumps into broken: eyes small and uneven,
  // the mechanism knocked askew.
  failed(t) {
    const flinch = smooth(seg(t, 0, 0.08)) * (1 - smooth(seg(t, 0.1, 0.3)));
    const shudder = t > 0.12 ? Math.sin((t - 0.12) * 42) * Math.exp(-(t - 0.12) * 3.2) : 0;
    const slump = smooth(seg(t, 0.7, 1.5));
    return {
      x: 1.2 * shudder, rot: 2 * shudder - 4 * slump, y: 0.5 * slump,
      sy: 1 - 0.06 * flinch - 0.04 * slump, sx: 1 + 0.04 * flinch + 0.03 * slump,
      wide: 0.6 * flinch, k: 1 + 0.15 * flinch, worry: smooth(seg(t, 0.2, 0.6)), bend: smooth(seg(t, 0.12, 0.5)),
      look: [0, 0.3 * slump], gazeW: 0,
    };
  },
};

const STATES = {};
for (const [name, f] of Object.entries(LOOP)) STATES[name] = (t, k) => f(frac(t / CYCLE[name]), k);
for (const [name, f] of Object.entries(ONCE)) STATES[name] = (t, k) => f(t, k);
const stateOf = name => STATES[name] ?? STATES.idle;

// MARK: - Companions
//
// Each companion reads the pose's mechanism channels its own way. Everything
// paints within 22 DIP of the box's centre: the drawer's window region is
// each bot's inscribed circle.
//   pivot    the body's foot, where tilt and squash happen
//   tilt     body tilt cap (deg); hop: body hop cap (DIP)
//   hopFloat how much of a poke's hop goes to a floating part instead
//   mech     where the mechanism is from the eyes (spawning looks there)
//   outDir   where its output goes (sending watches it)
//   emote    where its "?" and "!" pop up (at) and in which colour
//   eyes     { x, y, gap, w, h, travel } at rest

const n1 = v => { const r = Math.round(v * 10) / 10; return r === 0 ? '0' : String(r); };
// A succulent leaf from (cx, cy) toward a degrees: a lens of two arcs,
// from its base (hidden in the rosette) to a pointed tip.
function leaf(cx, cy, a, len, wid, from = 0.8) {
  if (len - from < 0.4 || wid < 0.2) return '';
  const c = Math.cos(rad(a)), s = Math.sin(rad(a)), L = len - from, h = wid / 2, R = n1((L * L / 4 + h * h) / (2 * h));
  const b = `${n1(cx + c * from)} ${n1(cy + s * from)}`;
  return `M${b}A${R} ${R} 0 0 1 ${n1(cx + c * len)} ${n1(cy + s * len)}A${R} ${R} 0 0 1 ${b}Z`;
}
const xy = p => `${n(p[0])} ${n(p[1])}`;
const mid = (p, q, t) => [lerp(p[0], q[0], t), lerp(p[1], q[1], t)];
// The point at t on a cubic p, and the cubic's first t as [c1, c2, end].
const bez = (p, t) => { const m = 1 - t; return [0, 1].map(i => m * m * m * p[0][i] + 3 * m * m * t * p[1][i] + 3 * m * t * t * p[2][i] + t * t * t * p[3][i]); };
function bezTo(p, t) {
  const a = mid(p[0], p[1], t), b = mid(p[1], p[2], t), c = mid(p[2], p[3], t), d = mid(a, b, t);
  return [a, d, mid(d, mid(b, c, t), t)];
}
// An arc of an ellipse (rx, ry) turned rot degrees about (cx, cy), from
// parameter a0 to a1 degrees, in pieces of at most a quarter turn.
function ellArc(cx, cy, rx, ry, rot, a0, a1) {
  const c = Math.cos(rad(rot)), s = Math.sin(rad(rot));
  const pt = a => { const x = rx * Math.cos(rad(a)), y = ry * Math.sin(rad(a)); return `${n(cx + x * c - y * s)} ${n(cy + x * s + y * c)}`; };
  const k = Math.max(1, Math.ceil(Math.abs(a1 - a0) / 90 - 1e-6));
  let d = `M${pt(a0)}`;
  for (let i = 1; i <= k; i++) d += `A${n(rx)} ${n(ry)} ${n(rot)} 0 ${a1 > a0 ? 1 : 0} ${pt(a0 + (a1 - a0) * i / k)}`;
  return d;
}

const OBJ = {
  // A terracotta pot holding a succulent whose rosette fans out like Claude's
  // star. The leaves fan, lean toward the work and twist; a flower stalk
  // grows with output and buds on each mark; a pup sprouts while spawning;
  // the rosette wilts and drops a leaf when it breaks, and twine ties the
  // leaves while it's locked.
  claude: {
    pivot: [22, 37.8], tilt: 6, hop: 1.6, hopFloat: 0,
    emote: { at: emoteAt([32.2, 10.4]), color: '#D97757' },
    mech: [-0.8, -0.7], outDir: [0.7, -0.7],
    eyes: { x: 22, y: 31.9, gap: 7, w: 3, h: 5.1, travel: 1.1 },
    draw(o, id, rim) {
      const C = { pot: '#D97757', lip: '#C4643F', back: '#5E9E62', front: '#7FB77E', heart: '#A8D5A2', bloom: '#F0846A', stalk: '#6FA56E', twine: '#8C5A2B', eye: '#33261F' };
      const close = smooth(seg(o.hold, 0.7, 1)), aim = clamp(o.aim, -1, 1), wilt = o.bend;
      const fan = 1 + 0.16 * clamp(o.spread) + 0.14 * o.play - 0.3 * clamp(-o.spread) - 0.5 * close + 0.12 * o.slack + 0.3 * wilt - 0.05 * o.float;
      const cx = 22 + 0.6 * aim, cy = 22.2 + 0.6 * clamp(o.press), grow = 1 + 0.05 * o.beat;
      const tone = c => mixHex(c, '#A89C5C', 0.55 * wilt);
      // A leaf's angle: fanned, leaning toward the work, twisting with turn
      // (the near leaves more), its side lifted by alt, the right waving to
      // send.
      const ang = (a0, depth) => {
        let a = -90 + (a0 + 90) * fan + 9 * aim + depth * o.turn;
        if (a0 < -90) a += 10 * Math.max(0, -o.alt);
        else if (a0 > -90) a -= 10 * Math.max(0, o.alt) + 14 * o.send;
        return a;
      };
      const back = [[-166, 12.3], [-128, 13.9], [-90, 14.5], [-52, 13.9], [-14, 12.3]].map(([a, L]) => leaf(cx, cy, ang(a, -0.2), L * grow, 5.3)).join('');
      const front = [[-147, 10.3], [-109, 11.9], [-71, 11.9]].map(([a, L]) => leaf(cx, cy, ang(a, 0.35), L * grow, 5)).join('');
      // The fourth near leaf breaks off: up out of the rosette, then down to
      // lie at the pot's foot.
      const q1 = smooth(seg(wilt, 0, 0.3)), q2 = seg(wilt, 0.3, 1);
      const b1 = mid([cx, cy], [cx + 5.4, cy - 7.6], q1), fx = lerp(b1[0], 21.6, q2), fy = lerp(b1[1], 36.8, q2 * q2);
      const fa = lerp(lerp(ang(-33, 0.35), -80, q1), -6, q2) + 50 * bump(q2);
      const fallen = leaf(fx, fy, fa, 10.3 * grow, 5, 0.8);
      const heart = leaf(cx, cy, -102 + 0.5 * o.turn, 7.2 * grow, 3.2) + leaf(cx, cy, -78 + 0.5 * o.turn, 7.2 * grow, 3.2);
      const parts = [{ d: back, fill: tone(C.back) }];
      // A flower stalk grows with output, a bud on each mark.
      const x = Math.max(clamp(o.out), o.marks / 3), L = clamp(8 * x) * 0.35 + 0.65 * clamp(x);
      if (L > 0.02) {
        const s0 = [cx + 0.8, cy - 3], lean = 9 * aim + 0.5 * o.turn;
        const sp = [[0, 0], [1.2, -7], [4.2, -10.6], [8.4, -11.6]].map(([dx, dy]) => turnPt(s0[0] + dx, s0[1] + dy, s0[0], s0[1], lean));
        const [c1, c2, e] = bezTo(sp, L);
        parts.push({ d: `M${xy(sp[0])}C${xy(c1)} ${xy(c2)} ${xy(e)}`, line: [C.stalk, 1.5] });
        for (let i = 0; i < 3; i++) {
          const a = clamp(o.marks - i), t = 0.5 + 0.2 * i;
          if (a > 0.02 && t <= L + 0.01) parts.push({ c: [...bez(sp, t), 0.8 + 0.9 * a], fill: C.bloom });
        }
      }
      parts.push({ d: front + (wilt < 0.02 ? fallen : ''), fill: tone(C.front) }, { d: heart, fill: tone(C.heart), bare: true });
      const pup = clamp(o.spread, 0, 1.2);
      if (pup > 0.1) parts.push({ d: [-150, -105, -60].map(a => leaf(10.8, 23.2, a, 5.8 * pup, 3.2 * pup, 0.3)).join(''), fill: C.heart });
      parts.push(
        { d: 'M11.2 26.4H32.8L30.8 35.8C30.6 37.2 29.9 37.8 28.6 37.8H15.4C14.1 37.8 13.4 37.2 13.2 35.8Z', fill: C.pot },
        { d: rrect(9.4, 21.8, 25.2, 5, 2), fill: C.lip },
      );
      if (wilt >= 0.02) parts.push({ d: fallen, fill: tone(C.front) });
      if (o.send > 0.02) parts.push({ c: [cx + 5.6 + 4 * o.send, cy - 8 - 3 * o.send, 1.2 * clamp(3 * o.send)], fill: C.bloom });
      if (close > 0.02) parts.push({ d: `M${n(cx - 4.4)} ${n(cy - 6.6)}C${n(cx - 1.6)} ${n(cy - 5.2)} ${n(cx + 1.6)} ${n(cy - 5.2)} ${n(cx + 4.4)} ${n(cy - 6.6)}`, line: [C.twine, 1.5], a: close });
      return { defs: '', body: body(parts, rim) + eyePair(o, this.eyes, C.eye) };
    },
  },

  // A dark planet ornament on a stand, its ring tilted like Grok's slash and
  // a small moon riding it. The ring tilts toward each bearing, the moon
  // circles while it waits, lights run along the ring with output, the moon
  // is thrown to send, more moons come while spawning; the ring cracks and
  // slips when it breaks, and closes into a flat slash while it's locked.
  grok: {
    pivot: [22, 37.4], tilt: 4, hop: 1.2, hopFloat: 0.8,
    emote: { at: emoteAt([12.4, 9.6]), color: '#3B3F4A' },
    mech: [0.8, -0.4], outDir: [0.8, -0.6],
    eyes: { x: 22, y: 18.8, gap: 5.8, w: 2.8, h: 5, travel: 1.1 },
    draw(o, id, rim) {
      const C = { planet: '#3B3F4A', ring: '#B9BFCB', moon: '#F3DFA2', stand: '#2A2D35', spark: '#FFB52E', eye: '#FFFFFF' };
      const close = smooth(seg(o.hold, 0.6, 1)), aim = clamp(o.aim, -1, 1);
      const up = clamp(0.9 * o.float + 0.8 * o.play - 0.5 * clamp(o.press), -0.8, 1.6);
      const cx = 22, cy = 20.4 - up, R = 9.4;
      const tilt = -30 + 9 * aim + 5 * o.alt + 4 * o.beat + 22 * o.bend + 6 * o.slack;
      const rx = 15.6 * (1 - 0.16 * clamp(-o.spread)), ry = 4.4 * (1 - 0.85 * close) + 0.4 * o.beat;
      const phi = 40 + Math.atan2(o.orb[1], o.orb[0]) * 180 / PI + 1.6 * o.turn + 360 * o.play + 120 * clamp(2 * Math.max(o.slack, o.bend));
      const c = Math.cos(rad(tilt)), s = Math.sin(rad(tilt));
      const on = a => { const x = rx * Math.cos(rad(a)), y = ry * Math.sin(rad(a)); return [cx + x * c - y * s, cy + x * s + y * c]; };
      const gap = 34 * o.bend;
      const front = gap > 1 ? ellArc(cx, cy, rx, ry, tilt, 0, 60 - gap / 2) + ellArc(cx, cy, rx, ry, tilt, 60 + gap / 2, 180) : ellArc(cx, cy, rx, ry, tilt, 0, 180);
      const moons = [[phi, 2.5, 1 - close]], sp = clamp(o.spread, 0, 1.2), back = [], fore = [];
      if (sp > 0.02) moons.push([phi + 120, 1.5 * sp, 1], [phi + 240, 1.5 * sp, 1]);
      moons.forEach(([a, r, al], i) => {
        let [x, y] = on(a);
        if (!i) { x += 2 * o.send; y -= 2.8 * o.send; }
        (Math.sin(rad(a)) < 0 ? back : fore).push({ c: [x, y, r], fill: C.moon, a: al });
      });
      const sparks = [50, 90, 130].map((a, i) => ({ c: [...on(a), 1.45 * clamp(o.marks - i)], fill: C.spark, bare: true, a: o.marks > i + 0.02 ? 1 : 0 }));
      const parts = [
        { d: rrect(20.8, cy + R - 1.5, 2.4, 35.6 - (cy + R - 1.5), 1.2), fill: C.stand },
        { d: rrect(15.6, 34.4, 12.8, 3.2, 1.6), fill: C.stand },
        { d: ellArc(cx, cy, rx, ry, tilt, 180, 360), line: [C.ring, 2.3] },
        ...back,
        { c: [cx, cy, R], fill: C.planet },
        { d: front, line: [C.ring, 2.3] },
        ...sparks,
        ...fore,
      ];
      return { defs: '', body: body(parts, rim) + eyePair(o, { ...this.eyes, y: this.eyes.y - up }, C.eye) };
    },
  },

};

// MARK: - Computer, saucer, submarine and helmet
//
// Codex's desk computer, Antigravity's flying saucer, DeepSeek's submarine
// and Kimi Code's astronaut helmet. Each reads the pose channels its own
// way and pops its "?" and "!" as an emote in its own colour.

// A >_ prompt at (x, y) at scale s, the output marks typed after it and the
// caret after them.
function promptMarks(x, y, s, col, w, marks, gap = 2.2, caretCol = col) {
  const x0 = x + 2.6 * s, k = clamp(marks, 0, 3);
  let d = `M${n(x)} ${n(y - 1.6 * s)}L${n(x + 1.5 * s)} ${n(y)}L${n(x)} ${n(y + 1.6 * s)}`;
  for (let i = 0; i < 3; i++) {
    const a = clamp(marks - i);
    if (a > 0.02) d += `M${n(x0 + gap * s * i)} ${n(y + 1.6 * s)}H${n(x0 + gap * s * i + 1.1 * s * a)}`;
  }
  const cx = x0 + gap * s * k;
  return P(d, S(col, w, 'round', true)) + P(`M${n(cx)} ${n(y + 1.6 * s)}H${n(cx + 2.6 * s)}`, S(caretCol, w));
}
// Markup turned deg degrees about (px, py), then moved by (tx, ty).
function turnG(deg, px, py, tx, ty, inner) {
  if (Math.abs(deg) < 0.05 && Math.abs(tx) < 0.03 && Math.abs(ty) < 0.03) return inner;
  const r = rad(deg), c = Math.cos(r), s = Math.sin(r);
  return `<g${matrix(c, s, -s, c, px - (c * px - s * py) + tx, py - (s * px + c * py) + ty)}>${inner}</g>`;
}
// Colour blends in tenths, so a loop's first and last frames match exactly.
const qMix = (a, b, t) => mixHex(a, b, Math.round(clamp(t) * 10) / 10);
const zig = pts => pts.map(([x, y], i) => `${i ? 'L' : 'M'}${n(x)} ${n(y)}`).join('');

Object.assign(OBJ, {
  // A little blue desk computer whose screen is its face. It types after
  // the >_ in the screen's corner, swivels on its neck toward each search,
  // rises on its neck while thinking, blinks its power light on the beat,
  // pops a window while spawning and sends a packet off; its screen cracks
  // when it breaks, asks for a password when it's locked and dims when
  // paused.
  computer: {
    pivot: [22, 37.8], tilt: 5, hop: 1.4, hopFloat: 0.6,
    emote: { at: emoteAt([32, 9]), color: '#2D8CFF' },
    mech: [-0.8, -0.8], outDir: [0.7, -0.8],
    eyes: { x: 22, y: 22.6, gap: 7, w: 2.8, h: 5, travel: 1.1 },
    draw(o, id, rim) {
      const C = { case: '#2D8CFF', dark: '#1F6FD6', screen: '#10284F', glow: '#8CD8FF', text: '#8CC2FF', led: '#9BF0C0', off: '#2E6B55', crack: '#CFE3FF' };
      const close = smooth(seg(o.hold, 0.7, 1)), aim = clamp(o.aim, -1, 1), sp = clamp(o.spread, 0, 1.2);
      const lift = clamp(0.8 * o.float + 0.8 * o.play - 0.4 * o.slack, -0.6, 1.4);
      const dx = 1.2 * aim + 0.04 * o.turn, top = 12 - lift;
      const screen = qMix(qMix(C.screen, '#1B3E78', clamp(0.4 * o.play + 0.25 * clamp(o.out))), '#08142A', 0.5 * o.slack);
      const parts = [
        { d: rrect(15, 34.4, 14, 3.4, 1.7), fill: C.dark },
        { d: rrect(19.4, 31.5 - lift, 5.2, 4 + lift, 1), fill: C.dark },
        { d: rrect(9 + dx, top, 26, 21, 6), fill: C.case },
        { d: rrect(12.4 + dx, top + 3, 19.2, 15, 3.6), fill: screen, bare: true },
        { c: [31.2 + dx, top + 19, 0.75], fill: qMix(C.off, C.led, clamp(0.4 + o.beat + o.press - o.slack)), bare: true },
      ];
      if (sp > 0.08) parts.push(
        { d: rrect(11.8 - 2.8 * sp, 11.2 - 2.2 * sp, 5.6 * sp, 4.4 * sp, 1.3 * sp), fill: C.case },
        { d: rrect(11.8 - 2 * sp, 11.2 - 1.4 * sp, 4 * sp, 2.8 * sp, 0.8 * sp), fill: screen, bare: true },
      );
      if (o.send > 0.04) {
        const k = clamp(3 * o.send), px = 29 + dx + 4 * o.send, py = top + 3 - 6 * o.send;
        parts.push({ d: rrect(px - 1.6 * k, py - 1.2 * k, 3.2 * k, 2.4 * k, 0.6 * k), fill: C.glow });
      }
      let over = '';
      if (o.bend > 0.05) over += P(zig([[27.4 + dx, top + 3.2], [25.6 + dx, top + 6.6], [27.8 + dx, top + 8.6], [26.4 + dx, top + 11.2]]), S(C.crack, 0.8 * clamp(2 * o.bend), 'round', true));
      over += promptMarks(14.4 + dx, top + 6, 0.62, qMix(C.text, '#3F6E9E', 0.6 * o.slack), 0.9, o.marks);
      const field = close > 0.3 ? P(rrect(15.4 + dx, top + 13.8, 13.2, 2.8, 1.4), F('#FFFFFF'))
        + [0, 1, 2].map(i => `<circle cx="${n(18.6 + dx + 2.2 * i)}" cy="${n(top + 15.2)}" r="0.55"${F(C.screen)}/>`).join('') : '';
      return { defs: '', body: body(parts, rim) + over + eyePair(o, { ...this.eyes, x: 22 + dx, y: top + 10.6 }, qMix(C.glow, '#3F6E9E', 0.5 * o.slack)) + field };
    },
  },

  // A violet flying saucer with a glass dome for a face. It hovers over a
  // soft beam, banks toward each search and floats higher while thinking;
  // its rim lights run round as it works and go once round while it waits,
  // each mark rises up the beam as a spark, a parcel is beamed up to send
  // and a little saucer launches while spawning. It lands with its lights
  // dim when paused, tilts with a bent antenna when it breaks, and is
  // tethered to the ground when it's locked.
  saucer: {
    pivot: [22, 36.6], tilt: 5, hop: 0.6, hopFloat: 1.4,
    emote: { at: emoteAt([32.5, 9.5]), color: '#8E63F5' },
    mech: [-0.8, -0.8], outDir: [0, 1],
    eyes: { x: 22, y: 19.6, gap: 5.6, w: 2.4, h: 4, travel: 0.8 },
    draw(o, id, rim) {
      const C = { hull: '#8E63F5', under: '#6440D0', dome: '#E6DCFF', beam: '#E3D8FF', eye: '#3A2A86', spark: '#FBBC05' };
      const LIGHTS = ['#EA4335', '#FBBC05', '#34A853', '#4285F4'];
      const close = smooth(seg(o.hold, 0.7, 1)), aim = clamp(o.aim, -1, 1), sp = clamp(o.spread, 0, 1.2);
      const up = clamp(1.3 * o.float + 1.2 * o.play + 0.8 * clamp(o.out) - 1 * o.slack - 0.6 * clamp(o.press) - 0.6 * o.bend, -1.2, 2.6) * (1 - 0.6 * close);
      const bank = 9 * aim + 6 * o.alt + 12 * o.bend;
      // The beam narrows to nothing before it goes, never a thin stick.
      const beamOn = clamp((clamp(1 - 1.8 * o.slack - 1.5 * o.bend - close) - 0.35) / 0.65);
      const out = [];
      if (beamOn > 0.05) {
        const top = 28.6 - up, hw = 9.4 * (1 + 0.12 * o.beat) * beamOn, tw = 6.2 * beamOn;
        out.push({ d: poly([[22 - tw, top], [22 + tw, top], [22 + hw, 36.6], [22 - hw, 36.6]]), fill: qMix(C.beam, '#F4EFFF', 0.6 * o.beat) });
        for (let i = 0; i < 3; i++) {
          const a = clamp(o.marks - i);
          if (a > 0.02) out.push({ c: [22 + [-2.6, 1.8, -0.4][i], 35 - 2.2 * i - 1.2 * a, 0.95 * a], fill: C.spark, bare: true });
        }
      }
      if (o.send > 0.04) {
        const k = clamp(3 * o.send), py = lerp(35, 31 - up, smooth(o.send));
        out.push({ d: rrect(22 - 1.3 * k, py - 1.3 * k, 2.6 * k, 2.6 * k, 0.6 * k), fill: C.spark });
      }
      if (close > 0.02) out.push({ d: `M22 ${n(30 - up)}V36.4`, line: [C.under, 0.9], a: close }, { d: rrect(20.6, 35.8, 2.8, 1.6, 0.8), fill: C.under, a: close });
      if (sp > 0.08) out.push({ d: ell(10.4, 11.8, 4.2 * sp, 1.4 * sp), fill: C.hull }, { d: `M${n(10.4 - 2.2 * sp)} ${n(11.4)}A${n(2.2 * sp)} ${n(2.2 * sp)} 0 0 1 ${n(10.4 + 2.2 * sp)} 11.4Z`, fill: C.dome });
      // The saucer: lights run round the rim (front half only).
      const spin = 4 * o.turn + 360 * o.play + Math.atan2(o.orb[1], o.orb[0]) * 180 / PI;
      const ant = o.bend > 0.05 ? [22 + 2.6 * o.bend, 11.2 + 0.6 * o.bend] : [22, 10.6];
      const parts = [
        { d: `M22 14.4L${n(ant[0])} ${n(ant[1])}`, line: [C.under, 1.3] },
        { c: [ant[0], ant[1] - 1, 1.5], fill: qMix('#7A6A2A', '#FBBC05', clamp(0.5 + o.beat - o.slack)) },
        { d: ell(22, 25, 15, 5), fill: C.hull },
        { d: ell(22, 27.1, 9, 2.3), fill: C.under, bare: true },
        { d: 'M13 23.6A9 9 0 0 1 31 23.6Z', fill: C.dome },
      ];
      LIGHTS.forEach((col, i) => {
        const t = rad(25 + 43 * i + spin), s = Math.sin(t);
        if (s > 0.12) parts.push({ c: [22 + 13 * Math.cos(t), 25.4 + 3.1 * s, 1.1], fill: qMix(col, C.under, clamp(0.6 * o.slack + (i === 2 ? o.bend : 0))), bare: true });
      });
      const saucer = body(parts, rim) + eyePair(o, this.eyes, C.eye);
      return { defs: '', body: body(out, rim) + turnG(bank, 22, 24, 0, -up, saucer) };
    },
  },

  // A little deep-sea submarine whose tail is a whale's flukes. It dives
  // and surfaces with the work, raises its periscope and turns it to each
  // search, sends up a bubble per mark and pings a sonar ring to send, and
  // a little sub slips out while spawning. Paused, it settles with its
  // periscope down and its lights dim; broken, the periscope kinks; locked,
  // it's anchored to the floor.
  submarine: {
    pivot: [21, 33], tilt: 4, hop: 1.2, hopFloat: 0,
    emote: { at: emoteAt([33, 9]), color: '#4D6BFE' },
    mech: [-0.8, -0.9], outDir: [-1, 0],
    eyes: { x: 15.8, y: 23.8, gap: 5.8, w: 2.6, h: 4.6, travel: 1 },
    draw(o, id, rim) {
      const C = { hull: '#4D6BFE', dark: '#3248B8', stripe: '#B7C4FF', water: '#BFD0FF' };
      const close = smooth(seg(o.hold, 0.7, 1)), aim = clamp(o.aim, -1, 1), sp = clamp(o.spread, 0, 1.2);
      const oy = -clamp(1.1 * o.float + 0.8 * o.play - 0.6 * o.slack - 0.5 * clamp(o.press), -0.8, 1.6);
      const h = (4.4 + 2.4 * clamp(Math.abs(aim) + 0.5 * o.play)) * (1 - 0.65 * Math.max(o.slack, close));
      const dir = Math.abs(aim) < 0.04 ? 1 : clamp(aim * 4, -1, 1);
      const top = 12 - h, kink = o.bend > 0.05;
      const scope = kink ? `M19.6 12V${n(top + 1.2)}L${n(19.6 + 2.6 * o.bend)} ${n(top + 2.6)}` : `M19.6 12V${n(top)}H${n(19.6 + 3.8 * dir)}`;
      const parts = [
        { d: scope, line: [C.dark, 1.6] },
        { d: rrect(15, 11.6, 9.4, 7.4, 2.6), fill: C.dark },
        { d: poly([[33, 24], [37.8, 19 - 1.2 * o.beat], [38.2, 29 + 1.2 * o.beat]]), fill: C.dark, edge: 1.4 },
        { d: pill(21, 24, 28, 14.4), fill: C.hull },
        { d: rrect(10, 27, 22, 1.6, 0.8), fill: qMix(qMix(C.stripe, '#FFFFFF', clamp(0.6 * clamp(o.out) + 0.4 * o.beat)), '#6F82E0', clamp(0.7 * o.slack + 0.6 * o.bend)), bare: true },
      ];
      const bubbles = Math.max(o.marks, 3 * o.play);
      [[36.2, 12.6, 1.3], [33.6, 10, 0.9], [31.4, 7.6, 1.1]].forEach(([x, y, r], i) => {
        const a = clamp(bubbles - i);
        if (a > 0.02) parts.push({ c: [x, y - 0.8 * a, r * a], fill: C.water });
      });
      if (o.send > 0.04) {
        const k = clamp(4 * o.send), w = 1.4 * k * (1 - 0.6 * o.send);
        parts.push({ d: arcD(10, 24, 2.4 + 2.6 * o.send, 150, 210), line: [C.water, w], bare: true }, { d: arcD(10, 24, 4.2 + 2.6 * o.send, 160, 200), line: [C.water, w * 0.8], bare: true });
      }
      const sub = body(parts, rim) + eyePair(o, this.eyes, '#FFFFFF');
      const out = [];
      if (sp > 0.08) out.push({ d: pill(10.6, 10.8, 8 * sp, 3.8 * sp), fill: C.hull }, { d: rrect(9.6, 10.8 - 3.2 * sp, 2.4 * sp, 1.8 * sp, 0.6 * sp), fill: C.dark });
      if (close > 0.02) out.push({ d: `M21 ${n(31 + oy)}V35.2`, line: [C.dark, 0.9], a: close }, { d: 'M18.8 35.2A2.4 2.4 0 0 0 23.2 35.2M21 34.6V37.2', line: [C.dark, 1.2], a: close });
      return { defs: '', body: body(out, rim) + turnG(0, 0, 0, 0, oy, sub) };
    },
  },

  // A space helmet with a navy visor for a face, Kimi-blue trim and a >_
  // collar. A scan light runs along the visor toward each search, the antenna
  // light blinks with each mark, it radios a pulse to send and a little
  // satellite pops out while spawning. It floats while it thinks; paused,
  // the visor dims; broken, the visor cracks and the antenna bends; locked,
  // the sun shutter slides down.
  helmet: {
    pivot: [22, 36.6], tilt: 5, hop: 1.4, hopFloat: 0,
    emote: { at: emoteAt([33, 8.6]), color: '#1D4E9E' },
    mech: [0.8, 0.8], outDir: [-0.7, -0.8],
    eyes: { x: 22, y: 20, gap: 6.6, w: 2.8, h: 5, travel: 1 },
    draw(o, id, rim) {
      const C = { shell: '#DCE3EE', visor: '#1D4E9E', trim: '#2E8FF7', light: '#FFD45C', shutter: '#C9D3E3' };
      const close = smooth(seg(o.hold, 0.7, 1)), aim = clamp(o.aim, -1, 1), sp = clamp(o.spread, 0, 1.2);
      const oy = -clamp(0.7 * o.float + 0.8 * o.play - 0.4 * o.slack, -0.6, 1);
      const ball = o.bend > 0.05 ? [11.4 + 0.6 * (1 - o.bend), 10.2] : [13.2, 8];
      const glow = clamp(0.35 + o.beat + 0.7 * bump(frac(o.marks)) - 0.6 * o.slack);
      const parts = [
        { d: `M14.6 10.4L${n(ball[0] + 0.2)} ${n(ball[1] + 0.4)}`, line: [C.visor, 1.3] },
        { c: [ball[0], ball[1], 1.3], fill: qMix('#6E6A4A', C.light, glow) },
        { c: [8.8, 20, 1.8], fill: qMix(C.trim, '#9CD0FF', 0.6 * clamp(o.out)) }, { c: [35.2, 20, 1.8], fill: qMix(C.trim, '#9CD0FF', 0.6 * clamp(o.out)) },
        { c: [22, 20, 13], fill: C.shell },
        { d: rrect(12, 31, 20, 5.6, 2.8), fill: C.visor },
        { d: rrect(12.4, 13, 19.2, 14, 7), fill: qMix(C.visor, '#0E2A55', 0.5 * o.slack), bare: true },
      ];
      if (o.send > 0.04) {
        const k = clamp(4 * o.send), w = 1 * k * (1 - 0.6 * o.send);
        parts.push({ d: arcD(ball[0], ball[1], 1.8 + 1.2 * o.send, 200, 250), line: [C.light, w], bare: true }, { d: arcD(ball[0], ball[1], 3 + 1.2 * o.send, 205, 245), line: [C.light, w * 0.8], bare: true });
      }
      let over = P(rrect(12.4, 13, 19.2, 14, 7), S(C.trim, 1.1)) + promptMarks(17.6, 33.8, 0.55, '#FFFFFF', 0.95, o.marks);
      if (Math.abs(aim) > 0.05) over += `<circle cx="${n(22 + 6.4 * aim)}" cy="25.2" r="0.9"${F('#7CC2FF')}/>`;
      if (o.bend > 0.05) over += P(zig([[27.6, 13.4], [25.8, 16.4], [28.4, 18], [26.8, 21]]), S('#CFE3FF', 0.8 * clamp(2 * o.bend), 'round', true));
      let shut = '';
      if (close > 0.02) shut = P(`M12.4 20A7 7 0 0 1 19.4 13H24.6A7 7 0 0 1 31.6 20V${n(13 + 9 * close)}H12.4Z`, F(C.shutter));
      const helmet = body(parts, rim) + over + eyePair(o, this.eyes, qMix('#FFFFFF', '#8FA3C7', 0.6 * o.slack)) + shut;
      const sat = sp > 0.08 ? body([{ d: rrect(34.4 - 3.4 * sp, 31 - 0.7 * sp, 6.8 * sp, 1.4 * sp, 0.4 * sp), fill: C.trim }, { d: rrect(34.4 - 1.2 * sp, 31 - 1.2 * sp, 2.4 * sp, 2.4 * sp, 0.6 * sp), fill: C.shell }], rim) : '';
      return { defs: '', body: sat + turnG(0, 0, 0, 0, oy, helmet) };
    },
  },
});

// MARK: - Kinds
//
// Each agent's companion, its colour for the app's chips, pucks and charts,
// and its idle habits. Cursor folds into Grok in the drawer, so its bot is
// Grok's planet, in its own colour.
const KIND = {
  claude: { object: 'claude', swatch: '#D97757', fidgets: ['fiddle', 'peek'] },
  codex: { object: 'computer', swatch: '#2D8CFF', fidgets: ['fiddle', 'hop'] },
  antigravity: { object: 'saucer', swatch: '#8E63F5', fidgets: ['fiddle', 'sway'] },
  grok: { object: 'grok', swatch: '#6E7480', fidgets: ['fiddle', 'peek'] },
  cursor: { object: 'grok', swatch: '#24252C', fidgets: ['fiddle', 'peek'] },
  kimi: { object: 'helmet', swatch: '#1D4E9E', fidgets: ['fiddle', 'sway'] },
  deepseek: { object: 'submarine', swatch: '#4D6BFE', fidgets: ['fiddle', 'peek'] },
};

// MARK: - Drawing
//
// A frame: the object under one body transform (tilt and squash at its
// foot, eased into its caps), then the badge, which doesn't tilt.

function draw(o, { id, rim, obj, size }) {
  const [px, py] = obj.pivot;
  const up = o.y < 0 ? -obj.hop * Math.tanh(-o.y / obj.hop) : Math.min(o.y, 1.2);
  const r = rad(soft(o.rot, obj.tilt)), co = Math.cos(r), si = Math.sin(r);
  const a = co * o.sx, b = si * o.sx, c = -si * o.sy, d = co * o.sy;
  const [dx, dy] = obj.offset ?? [0, 0];
  const e = px + o.x + dx - (a * px + c * py), f = py + up + dy - (b * px + d * py);
  const fr = obj.draw(o, id, rim);
  const still = n3(a) === '1' && n3(b) === '0' && n3(c) === '0' && n3(d) === '1' && n(e) === '0' && n(f) === '0';
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 44 44" width="${n(size)}" height="${n(size)}">`
    + (fr.defs ? `<defs>${fr.defs}</defs>` : '')
    + (still ? fr.body : `<g${matrix(a, b, c, d, e, f)}>${fr.body}</g>`) + badge(o, obj) + '</svg>';
}
