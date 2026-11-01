// The differential test's body: no host APIs, so Node (diff.mjs) and JavaScriptCore (jsc.mjs) both run it.
// runDiff(old, new, steps-per-run, seeds) throws on the first difference and returns the counts.
export function runDiff(A, B, steps, seeds) {
let checked = 0;
function fail(message, context) {
  throw new Error('MISMATCH: ' + message + ' ' + JSON.stringify(context));
}

// The module-level lookups agree.
const same = (what, a, b) => { if (JSON.stringify(a) !== JSON.stringify(b)) fail(what, { a, b }); };
same('KINDS', A.KINDS, B.KINDS);
same('PHASES', A.PHASES, B.PHASES);
same('CALM', A.CALM, B.CALM);
same('LOOKS', A.LOOKS, B.LOOKS);
for (const kind of A.KINDS) {
  for (const { id } of A.LOOKS[kind]) {
    same(`fidgetsOf ${kind}/${id}`, A.fidgetsOf(kind, id), B.fidgetsOf(kind, id));
    same(`swatchOf ${kind}/${id}`, A.swatchOf(kind, id), B.swatchOf(kind, id));
    for (const state of A.PHASES) {
      same(`cycleOf ${kind}/${id}/${state}`, A.cycleOf(kind, state, id), B.cycleOf(kind, state, id));
      same(`settleOf ${state}`, A.settleOf(state), B.settleOf(state));
    }
  }
  same(`nameOf ${kind}`, A.nameOf(kind), B.nameOf(kind));
}

function prng(seed) {
  let s = (seed >>> 0) || 1;
  return () => {
    s = (s + 0x6D2B79F5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const DTS = [0, 0.0001, 1 / 120, 1 / 60, 1 / 30, 1 / 30, 1 / 30, 0.1, 0.25, 0.4, 1.7];
const SIZES = [44, 44, 44, 48, 22, 88.5];
const IDS = ['b0', 'b0', 'b3', 'x'];

function compare(label, bots, size, id, context) {
  const [a, b] = bots;
  if (a.state !== b.state) fail(`${label}: state`, { ...context, a: a.state, b: b.state });
  if (a.calm !== b.calm) fail(`${label}: calm`, { ...context, a: a.calm, b: b.calm });
  const sa = a.svg(size, id), sb = b.svg(size, id);
  checked++;
  if (sa !== sb) {
    let at = 0;
    while (at < sa.length && sa[at] === sb[at]) at++;
    fail(`${label}: svg differs at char ${at}`, { ...context, old: sa.slice(Math.max(0, at - 80), at + 120), new: sb.slice(Math.max(0, at - 80), at + 120), oldLength: sa.length, newLength: sb.length });
  }
}

let runs = 0;
for (const kind of A.KINDS) {
  for (const { id: look } of A.LOOKS[kind]) {
    for (let seedIndex = 0; seedIndex < seeds; seedIndex++) {
      const rand = prng(1000 * seedIndex + 17 + kind.length * 31 + look.length);
      const pick = list => list[Math.floor(rand() * list.length)];
      const options = { seed: 1 + Math.floor(rand() * 5000), tempo: pick([1, 1, 0.8, 1.3]), rim: rand() < 0.85, look };
      const bots = [A.createBot(kind, options), B.createBot(kind, options)];
      const fidgets = A.fidgetsOf(kind, look);
      let size = 44, id = 'b0';
      const log = [];
      const context = () => ({ kind, look, options, recent: log.slice(-6) });
      compare('created', bots, size, id, context());
      for (let step = 0; step < steps; step++) {
        const roll = rand();
        let what;
        if (roll < 0.62) {
          const dt = pick(DTS);
          what = `advance ${dt}`;
          for (const bot of bots) bot.advance(dt);
        } else if (roll < 0.72) {
          const state = rand() < 0.2 && fidgets.length ? pick(fidgets) : pick(A.PHASES);
          const phase = rand() < 0.5 ? -1 : Math.round(rand() * 100) / 100;
          what = `setState ${state} ${phase}`;
          for (const bot of bots) bot.setState(state, phase);
        } else if (roll < 0.80) {
          const dx = (rand() - 0.5) * pick([20, 80, 400]), dy = (rand() - 0.5) * pick([20, 80, 400]);
          what = `setGaze ${dx.toFixed(2)} ${dy.toFixed(2)}`;
          for (const bot of bots) bot.setGaze(dx, dy);
        } else if (roll < 0.84) {
          what = 'clearGaze';
          for (const bot of bots) bot.clearGaze();
        } else if (roll < 0.88) {
          what = 'poke';
          for (const bot of bots) bot.poke();
        } else if (roll < 0.90) {
          const on = rand() < 0.5;
          what = `setRim ${on}`;
          for (const bot of bots) bot.setRim?.(on);
        } else if (roll < 0.95) {
          size = pick(SIZES);
          id = pick(IDS);
          what = `ask size ${size} id ${id}`;
        } else {
          what = 'ask again';
        }
        log.push(what);
        compare(what, bots, size, id, context());
        // And the same picture again, which a memo would be asked to return.
        if (rand() < 0.25) compare(`${what} (again)`, bots, size, id, context());
      }
      runs++;
    }
  }
}
return { runs, checked };
}
