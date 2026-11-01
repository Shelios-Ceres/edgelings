// Primitive costs on this script engine, as the Pixel engine uses them: microseconds per run of a pattern.
function bench(name, fn, reps = 3000) {
  for (let i = 0; i < 300; i++) fn();
  const start = preciseTime();
  for (let i = 0; i < reps; i++) fn();
  print(name.padEnd(46) + ((preciseTime() - start) * 1e6 / reps).toFixed(2).padStart(9) + ' us');
}
const N8 = [[1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [-1, -1], [1, -1], [-1, 1]];
const cellsList = Array.from({ length: 150 }, (_, i) => ((i % 15) + 3) + ((Math.floor(i / 15) + 3) << 11));
let sink = 0;
bench('Map.set 150 int keys', () => { const m = new Map(); for (let i = 0; i < 150; i++) m.set(cellsList[i], '#fff'); sink += m.size; });
const big = new Map(); for (let i = 0; i < 150; i++) big.set(cellsList[i], i % 3 ? '#fff' : '#000');
bench('for (const [k,c] of map) x150', () => { let t = 0; for (const [k, c] of big) t += k; sink += t; });
bench('map.forEach x150', () => { let t = 0; big.forEach((c, k) => { t += k; }); sink += t; });
bench('for (const k of map.keys()) x150', () => { let t = 0; for (const k of big.keys()) t += k; sink += t; });
bench('[...map.keys()].every x150', () => { sink += [...big.keys()].every(k => k > 0) ? 1 : 0; });
bench('for (const [dx,dy] of N8) x8 x150', () => { let t = 0; for (let i = 0; i < 150; i++) for (const [dx, dy] of N8) t += dx + dy; sink += t; });
bench('indexed loop over N8 x8 x150', () => { let t = 0; for (let i = 0; i < 150; i++) for (let j = 0; j < 8; j++) { const o = N8[j]; t += o[0] + o[1]; } sink += t; });
bench('map.has x1200', () => { let t = 0; for (let i = 0; i < 1200; i++) if (big.has(cellsList[i % 150] + (i & 3))) t++; sink += t; });
bench('Set.add 300', () => { const s = new Set(); for (let i = 0; i < 300; i++) s.add(i * 7); sink += s.size; });
const row = '.##..#.#';
bench('rows.forEach([...row].forEach) 12 rows', () => { let t = 0; for (let r = 0; r < 12; r++) [...row].forEach((ch, i) => { if (ch === '#') t += i; }); sink += t; });
bench('indexed row loop 12 rows', () => { let t = 0; for (let r = 0; r < 12; r++) for (let i = 0; i < row.length; i++) if (row[i] === '#') t += i; sink += t; });
const pose = { a: 1, b: 2, c: 3, d: 4, e: [0, 0], f: 'x', g: 5, h: 6, i: 7, j: 8, k: 9, l: 10, m: 11, n: 12, o: 13, p: 14, q: 15, r: 16, s: 17, t: 18 };
bench('{...pose} (20 keys)', () => { const p = { ...pose }; sink += p.a; });
bench('{...rest, ...pose}', () => { const p = { ...pose, ...pose, z: 1 }; sink += p.a; });
const n = v => { const r = Math.round(v * 100) / 100; return r === 0 ? '0' : String(r); };
bench('path string 100 runs with n()', () => { let d = ''; for (let i = 0; i < 100; i++) d += `M${n(i * 2)} ${n(i)}H${n(i * 2 + 2)}V${n(i + 1)}H${n(i * 2)}Z`; sink += d.length; });
bench('path string 100 runs, String()', () => { let d = ''; for (let i = 0; i < 100; i++) d += 'M' + (i * 2) + ' ' + i + 'H' + (i * 2 + 2) + 'V' + (i + 1) + 'H' + (i * 2) + 'Z'; sink += d.length; });
bench('Array.sort numbers (12 items) x10', () => { for (let r = 0; r < 10; r++) { const a = [5, 3, 9, 1, 7, 2, 8, 4, 6, 0, 11, 10]; a.sort((x, y) => x - y); sink += a[0]; } });
bench('typed array mask 150 cells', () => { const m = new Int32Array(64); for (let i = 0; i < 150; i++) m[(cellsList[i] >> 11)] |= 1 << (cellsList[i] & 31); sink += m[3]; });
bench('Math.sin/exp 200', () => { let t = 0; for (let i = 0; i < 200; i++) t += Math.sin(i * 0.1) * Math.exp(-i * 0.01); sink += t; });
bench('closure creation + call x100', () => { let t = 0; for (let i = 0; i < 100; i++) { const f = x => x + i; t += f(1); } sink += t; });
print('sink ' + sink);
