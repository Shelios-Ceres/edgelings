#!/usr/bin/env python3
"""Writes a synthetic home with the data sizes of a real developer Mac, so a scan can be timed at
real sizes. Made-up data only, deterministic. Only names, counts and sizes matter: the files hold
the minimum each reader needs.

  Antigravity   ~/.gemini/antigravity: conversations under brain/ (each with a transcript_full.jsonl)
                and a lot of other files (default 15,348 in all), plus antigravity-cli (128)
  Kimi Code     ~/.kimi-code/sessions: 80 wire.jsonl across 5 workspaces, 7 written in the last day
  Cursor        .../Cursor/User/globalStorage/state.vscdb with 333 bubbles that carry a token count of 0
  Codex         ~/.codex/sessions (1 file) and archived_sessions (2)

usage: make-big-home.py <home> [--layout mac|windows] [--antigravity-files 15348] [--kimi 80]
<home> is the folder EDGELINGS_ROOT/home points at.
"""
import argparse, json, os, random, sqlite3, time, uuid

parser = argparse.ArgumentParser()
parser.add_argument("home")
parser.add_argument("--layout", default="mac", choices=["mac", "windows"])
parser.add_argument("--antigravity-files", type=int, default=15348)
parser.add_argument("--antigravity-conversations", type=int, default=20)
parser.add_argument("--kimi", type=int, default=80)
parser.add_argument("--bubbles", type=int, default=333)
args = parser.parse_args()

home = args.home
rng = random.Random(20261102)
now = time.time()
app_data = os.path.join(home, "Library", "Application Support") if args.layout == "mac" else os.path.join(home, "AppData", "Roaming")


def write(path, data, mtime=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb" if isinstance(data, bytes) else "w") as f:
        f.write(data)
    if mtime is not None:
        os.utime(path, (mtime, mtime))


# --- Antigravity: conversations with a transcript, and the files around them -----------------------------
root = os.path.join(home, ".gemini", "antigravity")
conversations = [str(uuid.UUID(int=rng.getrandbits(128))) for _ in range(args.antigravity_conversations)]
written = 0
for n, conversation in enumerate(conversations):
    steps = []
    for i in range(60):
        steps.append({"source": "USER_EXPLICIT" if i % 20 == 0 else "MODEL", "step_index": i,
                      "created_at": int((now - 86400 * (n + 1) + i * 30) * 1000),
                      "type": "PLANNER_RESPONSE" if i % 2 else "RUN_COMMAND", "content": "x" * rng.randint(20, 300)})
    path = os.path.join(root, "brain", conversation, ".system_generated", "logs", "transcript_full.jsonl")
    write(path, "\n".join(json.dumps(s) for s in steps) + "\n", now - 86400 * (n + 1))
    written += 1
other = max(0, args.antigravity_files - written)
folders = ["conversations", "code_tracker/active", "knowledge", "annotations", "implicit", "scratch", "html_artifacts", "context_state"]
for i in range(other):
    folder = folders[i % len(folders)]
    sub = f"{rng.getrandbits(32):08x}" if "/" in folder or i % 3 == 0 else ""
    name = f"f{i:06d}.{['pb', 'md', 'json', 'txt'][i % 4]}"
    write(os.path.join(root, folder, sub, name), os.urandom(rng.randint(64, 2048)), now - rng.randint(3600, 86400 * 60))
# antigravity-cli: 128 files, one changed in the last day
cli = os.path.join(home, ".gemini", "antigravity-cli")
for i in range(128):
    write(os.path.join(cli, "files", f"c{i:04d}.bin"), os.urandom(256), now - (600 if i == 0 else rng.randint(86400 * 2, 86400 * 30)))
db = os.path.join(root, "conversation_summaries.db")
os.makedirs(root, exist_ok=True)
con = sqlite3.connect(db)
con.execute("create table if not exists conversation_summaries (conversation_id text, title text, workspace_uris text)")
for conversation in conversations:
    con.execute("insert into conversation_summaries values (?,?,?)", (conversation, "a conversation", "file:///fixture/project"))
con.commit()
con.close()

# --- Kimi Code ------------------------------------------------------------------------------------------
models = ["kimi-code/k3", "kimi-code/k3.1-pro"]
kimi_root = os.path.join(home, ".kimi-code")
for n in range(args.kimi):
    sid = str(uuid.UUID(int=rng.getrandbits(128)))
    folder = os.path.join(kimi_root, "sessions", f"wd_project{n % 5}_{n % 5:012x}", f"session_{sid}")
    t = int((now - 3600 * (2 if n < 7 else 24 * 3 + n)) * 1000)
    out = []
    for i in range(300):
        t += rng.randint(500, 4000)
        if i % 4 == 3:
            out.append({"type": "usage.record", "time": t, "model": rng.choice(models), "agentId": "main", "usageScope": "turn",
                        "usage": {"inputOther": rng.randint(200, 9000), "output": rng.randint(50, 2500), "inputCacheRead": rng.randint(0, 120000), "inputCacheCreation": 0}})
        elif i % 4 == 0:
            out.append({"type": "agent.turn.started", "time": t})
        else:
            out.append({"type": "context.append_loop_event", "time": t, "event": {"type": "content.part", "part": {"type": "text", "text": "x" * rng.randint(100, 900)}}})
    out.append({"type": "turn.ended", "time": t + 500, "reason": "completed"})
    mtime = now - 3600 * (2 if n < 7 else 24 * 3 + n)
    write(os.path.join(folder, "state.json"), json.dumps({"id": f"session_{sid}", "cwd": f"/fixture/project{n % 5}"}), mtime)
    write(os.path.join(folder, "agents", "main", "wire.jsonl"), "\n".join(json.dumps(line) for line in out) + "\n", mtime)
write(os.path.join(kimi_root, "config.toml"), '[models."kimi-code/k3"]\ndisplay_name = "K3"\n')

# --- Cursor: a ledger of bubbles that carry a token count of 0 ---------------------------------------------
cursor_db = os.path.join(app_data, "Cursor", "User", "globalStorage", "state.vscdb")
os.makedirs(os.path.dirname(cursor_db), exist_ok=True)
con = sqlite3.connect(cursor_db)
con.execute("create table if not exists cursorDiskKV (key text unique on conflict replace, value blob)")
for i in range(args.bubbles):
    bubble = {"createdAt": int((now - 3600 * i) * 1000), "tokenCount": {"inputTokens": 0, "outputTokens": 0}, "text": "x" * 300}
    con.execute("insert into cursorDiskKV values (?, ?)", (f"bubbleId:{uuid.UUID(int=rng.getrandbits(128))}:{i}", json.dumps(bubble)))
for i in range(4000):
    con.execute("insert into cursorDiskKV values (?, ?)", (f"composerData:{i}", os.urandom(600)))
con.commit()
con.close()

# --- Codex -----------------------------------------------------------------------------------------------
codex = os.path.join(home, ".codex")
write(os.path.join(codex, "sessions", "2026", "11", "01", f"rollout-{uuid.UUID(int=rng.getrandbits(128))}.jsonl"),
      json.dumps({"type": "session_meta", "timestamp": "2026-11-01T10:00:00Z", "payload": {"id": "fixture", "cwd": "/fixture/project0"}}) + "\n", now - 86400 * 2)
for i in range(2):
    write(os.path.join(codex, "archived_sessions", f"rollout-{i}.jsonl"), "{}\n", now - 86400 * 10)

total = sum(len(files) for _, _, files in os.walk(os.path.join(home, ".gemini", "antigravity")))
print(f"big home in {home}: antigravity {total} files, antigravity-cli 128, kimi {args.kimi} wires, cursor {args.bubbles} bubbles")
