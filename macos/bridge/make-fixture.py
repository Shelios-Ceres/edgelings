#!/usr/bin/env python3
"""Writes a synthetic Kimi Code data folder: sessions with usage records, so the
bridge has something to scan. Deterministic, made-up data only.

usage: make-fixture.py <folder> [sessions] [lines-per-session]
"""
import json, os, random, sys, time, uuid

root = sys.argv[1]
sessions = int(sys.argv[2]) if len(sys.argv) > 2 else 200
lines = int(sys.argv[3]) if len(sys.argv) > 3 else 400
rng = random.Random(20261030)
now = int(time.time() * 1000)
models = ["kimi-code/k3", "kimi-code/k3-256k", "kimi-code/k3.1-pro", "deepseek/deepseek-flash"]

os.makedirs(os.path.join(root, "sessions"), exist_ok=True)
with open(os.path.join(root, "config.toml"), "w") as f:
    f.write('[models."deepseek/deepseek-flash"]\nprovider = "deepseek"\nmodel = "deepseek-flash"\n')

for n in range(sessions):
    sid = str(uuid.UUID(int=rng.getrandbits(128)))
    folder = os.path.join(root, "sessions", f"wd_project{n % 12}_{n % 12:012x}", f"session_{sid}")
    agent = os.path.join(folder, "agents", "main")
    os.makedirs(agent, exist_ok=True)
    with open(os.path.join(folder, "state.json"), "w") as f:
        json.dump({"id": f"session_{sid}", "cwd": f"/fixture/project{n % 12}"}, f)
    t = now - rng.randint(60_000, 20 * 3_600_000)
    out = []
    for i in range(lines):
        t += rng.randint(500, 4000)
        if i % 4 == 3:
            out.append({"type": "usage.record", "time": t, "model": rng.choice(models), "agentId": "main", "usageScope": "turn",
                        "usage": {"inputOther": rng.randint(200, 9000), "output": rng.randint(50, 2500),
                                  "inputCacheRead": rng.randint(0, 120000), "inputCacheCreation": 0}})
        elif i % 4 == 0:
            out.append({"type": "agent.turn.started", "time": t})
        else:
            out.append({"type": "context.append_loop_event", "time": t, "event": {"type": "content.part", "part": {"type": "text", "text": "x" * rng.randint(20, 400)}}})
    out.append({"type": "turn.ended", "time": t + 500, "reason": "completed"})
    with open(os.path.join(agent, "wire.jsonl"), "w") as f:
        f.write("\n".join(json.dumps(line) for line in out) + "\n")
print(f"{sessions} sessions, {lines} lines each, in {root}")
