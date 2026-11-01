const states = ['idle', 'thinking', 'writing', 'running', 'sleeping'];
let frames = 0, total = 0;
const sums = {};
for (const kind of B.KINDS) {
  if (kind === 'cursor') continue;
  for (const { id } of B.LOOKS[kind]) for (const state of states) {
    const bot = B.createBot(kind, { seed: 7, tempo: 1, rim: true, look: id });
    bot.setState(state, 0);
    for (let i = 0; i < 20; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
    for (const key in PROF) PROF[key] = 0;
    const start = preciseTime();
    for (let i = 0; i < 40; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
    total += preciseTime() - start;
    for (const key in PROF) sums[key] = (sums[key] || 0) + PROF[key];
    frames += 40;
  }
}
print('per frame (advance + svg), mean over all looks and 5 states: ' + (total * 1000 / frames).toFixed(3) + ' ms');
for (const key in sums) print('  ' + key.padEnd(9) + (sums[key] * 1000 / frames).toFixed(3) + ' ms');
