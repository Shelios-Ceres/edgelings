// Times the Pixel engine's advance() and svg() for every look in a few states,
// the way the drawer calls them (one tick: advance, then svg).
//   node profile.mjs [path-to-engine.mjs] [rounds]
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const file = path.resolve(process.argv[2] ?? 'characters.frozen.mjs');
const rounds = Number(process.argv[3] ?? 300);
const lib = await import(pathToFileURL(file).href);

const states = ['idle', 'thinking', 'writing', 'running', 'sleeping'];
let total = 0, calls = 0;
const perKind = {};
for (const kind of lib.KINDS) {
  if (kind === 'cursor') continue;
  for (const { id } of lib.LOOKS[kind]) {
    for (const state of states) {
      const bot = lib.createBot(kind, { seed: 7, tempo: 1, rim: true, look: id });
      bot.setState(state, 0);
      for (let i = 0; i < 30; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
      const start = process.hrtime.bigint();
      for (let i = 0; i < rounds; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
      const ms = Number(process.hrtime.bigint() - start) / 1e6;
      total += ms; calls += rounds;
      (perKind[`${kind}/${id}`] ??= []).push(ms / rounds);
    }
  }
}
for (const [name, list] of Object.entries(perKind)) {
  console.log(name.padEnd(26), list.map(v => v.toFixed(3)).join('  '), ' ms per advance+svg (idle thinking writing running sleeping)');
}
console.log(`mean ${(total / calls).toFixed(3)} ms per advance+svg over ${calls} calls`);
