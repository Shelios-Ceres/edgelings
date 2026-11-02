# Edgelings: read-only probe

Run these in Terminal (or let your agent run them) and paste the output into your report. Every command only lists **names, counts and times**. None opens a file, and none prints anything from inside a file. Please do not add `cat`, `head` or `tail` on any file under `~/.kimi-code`, `~/Library/Application Support` or anywhere else: those can hold conversations and sign-in tokens.

Before pasting, replace your user name in any path with `~`.

## 1. Which Kimi and Codex programs are running

```sh
ps -axo comm= | grep -i -E 'kimi|codex' | sort | uniq -c
```

On macOS `comm=` is the program's path, so this shows both the name and where it lives. Run it once while **Kimi Code (the desktop app)** is open and once while only the **`kimi` command in a terminal** is running, if you can.

## 2. Who started the Codex process

```sh
for p in $(pgrep -i codex); do ps -o pid=,comm= -p "$p"; ps -o comm= -p "$(ps -o ppid= -p "$p" | tr -d ' ')"; done
```

Each pair of lines is a `codex` process and then its parent. A parent that is Cursor or Code means it is that editor's extension, not the Codex app or CLI.

## 3. Kimi Code's data folder

```sh
ls -la ~/.kimi-code ~/.kimi-code/sessions | head -40
ls ~/.kimi-code/server ~/.kimi-code/server/instances 2>&1 | head
find ~/.kimi-code/sessions -name wire.jsonl -mtime -1 | wc -l
```

The last line counts session logs changed in the last 24 hours. Run it while a Kimi Code session is working; a non-zero number means the logs are where Edgelings looks.

## 4. Does the Kimi desktop app keep its own folder?

```sh
ls ~/Library/Application\ Support | grep -i -E 'kimi|moonshot'
```

For every folder this prints, list the folder names two levels down (names only):

```sh
find ~/Library/Application\ Support/<the folder name> -maxdepth 2 -type d | head -60
```

## 5. Power source (for the CPU test)

```sh
pmset -g batt | head -1
```

## 7. The Kimi desktop app's Kimi Work data (if you have the Kimi client)

Edgelings reads Kimi Work conversations as Kimi sessions. On Windows they live under `kimi-desktop\daimon-share\daimonuntime\kimi-code\home\sessions`; this finds where they are on a Mac. Names only (the folders are named with ids, which is fine to paste).

```sh
ls ~/Library/Application\ Support | grep -i -E 'kimi|moonshot'
```

For the Kimi desktop client's folder that this prints (not Kimi Code's own), replace `<folder>` below:

```sh
D=~/Library/Application\ Support/<folder>/daimon-share/daimon/runtime/kimi-code/home/sessions
ls -d "$D"
find "$D" -maxdepth 2 -type d | sed 's#.*/sessions#sessions#' | head -60
find "$D" -name wire.jsonl -mtime -1 | wc -l
```

If the first `ls -d` says "No such file", run this instead and paste what it prints (folder names, three levels down):

```sh
find ~/Library/Application\ Support/<folder> -maxdepth 3 -type d | sed "s#$HOME#~#" | head -80
```

Run section 1 once more while the **Kimi** app (the desktop client, not Kimi Code) is open, and say which line in its output is the client.

## 6. Cursor: why tokens are zero (optional)

```sh
sqlite3 -readonly ~/Library/Application\ Support/Cursor/User/globalStorage/state.vscdb "select count(*) from cursorDiskKV where key like 'bubbleId:%' and instr(value,'tokenCount')>0;"
```

This prints one number: how many Cursor messages carry a token count at all. The Edgelings diagnostic prints the same facts, so you can skip this one.
