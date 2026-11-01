// Runs under JavaScriptCore's own shell (macOS: .../JavaScriptCore.framework/Helpers/jsc -m jsc.mjs):
// times the frozen Pixel engine against the live one, per look, the way the
// Mac drawer calls them, then runs the differential test on the same engine.
// The Mac app runs the engines on JavaScriptCore, which prices the same code
// differently from V8.
const A = await import('./characters.frozen.mjs');
const B = await import('../../src/AgentIsland.WinUI/Drawer/Characters/characters.mjs');
const { runDiff } = await import('./diff-core.mjs');

const states = ['idle', 'thinking', 'writing', 'running', 'sleeping'];
const rounds = 150;

function time(lib, kind, look, state) {
  const bot = lib.createBot(kind, { seed: 7, tempo: 1, rim: true, look });
  bot.setState(state, 0);
  for (let i = 0; i < 40; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
  const start = preciseTime();
  for (let i = 0; i < rounds; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
  return (preciseTime() - start) * 1000 / rounds;
}

let oldTotal = 0, newTotal = 0, count = 0;
for (const kind of A.KINDS) {
  if (kind === 'cursor') continue;
  for (const { id } of A.LOOKS[kind]) {
    let o = 0, n = 0;
    for (const state of states) { o += time(A, kind, id, state); n += time(B, kind, id, state); }
    o /= states.length; n /= states.length;
    oldTotal += o; newTotal += n; count++;
    print(`${(kind + '/' + id).padEnd(24)} old ${o.toFixed(3)} ms  new ${n.toFixed(3)} ms  x${(o / n).toFixed(2)}`);
  }
}
print(`mean per advance+svg under JavaScriptCore: old ${(oldTotal / count).toFixed(3)} ms, new ${(newTotal / count).toFixed(3)} ms, x${(oldTotal / newTotal).toFixed(2)}`);

const { runs, checked } = runDiff(A, B, 500, 2);
print(`engine differential under JavaScriptCore: ${runs} runs, ${checked} SVG comparisons, all identical`);
