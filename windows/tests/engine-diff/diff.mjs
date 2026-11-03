// Differential test of the Pixel engine: the frozen file (characters.frozen.mjs)
// and the current one run side by side through long randomized sequences for
// every look of every agent. Every SVG string, and every getter, must be equal
// at every step.
//
//   node diff.mjs [old.mjs] [new.mjs] [steps-per-run] [seeds]
//
// A run = one look, one seed, one set of options (tempo, rim, size, id). Each
// step is one of: advance by a random dt (sometimes 0, tiny or past the
// engine's 0.25 s clamp), enter a random phase (from a random point or the
// start), move or clear the gaze, poke, switch the rim, enter one of the
// look's own fidgets, or ask for the SVG again (same time, or another size or
// id). A bot comparison follows every step.
import { pathToFileURL } from 'node:url';
import path from 'node:path';
import fs from 'node:fs';
import os from 'node:os';

const here = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const oldFile = path.resolve(process.argv[2] ?? path.join(here, 'characters.frozen.mjs'));
const newFile = path.resolve(process.argv[3] ?? path.join(here, '../../src/AgentIsland.WinUI/Drawer/Characters/characters.mjs'));
const steps = Number(process.argv[4] ?? 2000);
const seeds = Number(process.argv[5] ?? 4);

const A = await import(pathToFileURL(oldFile).href);
const B = await import(pathToFileURL(newFile).href);

import { runDiff } from './diff-core.mjs';
try {
  const { runs, checked, oldThrew } = runDiff(A, B, steps, seeds);
  console.log(`engine differential: ${runs} runs, ${checked} SVG comparisons, all identical${oldThrew ? `, the old engine threw on ${oldThrew} (the new one drew them)` : ''} (old ${path.basename(oldFile)} vs new ${path.basename(newFile)})`);
  // The ring marks wrap: run again with the live engine's stamp counter a few calls short of what an
  // Int32Array holds, so every look crosses the wrap.
  const source = fs.readFileSync(newFile, 'utf8');
  if (!source.includes('let ringStamp = 0;')) throw new Error('ringStamp not found: update this test with the engine');
  const wrapFile = path.join(os.tmpdir(), `characters-ringstamp-${process.pid}.mjs`);
  fs.writeFileSync(wrapFile, source.replace('let ringStamp = 0;', 'let ringStamp = 2147483000;'));
  try {
    const W = await import(pathToFileURL(wrapFile).href);
    const wrapped = runDiff(A, W, 400, 1);
    console.log(`engine differential across the ring stamp wrap: ${wrapped.runs} runs, ${wrapped.checked} SVG comparisons, all identical${wrapped.oldThrew ? `, the old engine threw on ${wrapped.oldThrew}` : ''}`);
  } finally {
    fs.rmSync(wrapFile, { force: true });
  }
} catch (error) {
  console.error(String(error.message));
  process.exit(1);
}
