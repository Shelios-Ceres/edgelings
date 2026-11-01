# Pixel engine differential test

`characters.frozen.mjs` is the Pixel engine as the Pixel design chat froze it (v5.4, sha256 `6c826907...`). `diff.mjs` runs it side by side with the live engine (`windows/src/AgentIsland.WinUI/Drawer/Characters/characters.mjs`) through long randomized sequences for every look of every agent: advances (including 0, tiny and past the 0.25 s clamp), phase changes from a random point or the start, gaze moves, pokes, the rim switch, the look's own fidgets, and size and id changes. It asks for each picture twice. **Every SVG string, and the `state` and `calm` getters, must be equal at every step**, and the module-level lookups (`KINDS`, `PHASES`, `CALM`, `LOOKS`, `fidgetsOf`, `swatchOf`, `cycleOf`, `settleOf`, `nameOf`) must match.

```sh
node windows/tests/engine-diff/diff.mjs [old.mjs] [new.mjs] [steps-per-run] [seeds]
node windows/tests/engine-diff/profile.mjs [engine.mjs] [rounds]    # ms per advance + svg
```

The frozen copy is a fixture: never edit it. When the Pixel chat freezes a later engine, copy that file over it and record its sha here.

The live engine was made faster without changing a byte of output: a canvas keys its cells by integer instead of `"x,y"` text (the Map keeps its insertion order, so the order cells are visited in is unchanged; a cell at an odd coordinate keeps a text key), the white rim is marked on a small grid, and a row of cells is a bit mask whose runs are read off by scanning bits.
