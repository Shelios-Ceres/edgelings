# How Agent Island Detects Claude Code and Codex Session State

> Upstream note, written for Agent Island 2.1.2 on macOS and for Claude Code and Codex. The Windows app in this repository reads more agents; the [README](../README.md) lists them and what it reads.

Agent Island is built around a small problem that becomes painful during long agentic coding runs: the task does not always fail loudly. A Claude Code or Codex session may still be working, may have finished and be waiting for your next instruction, or may have frozen mid-turn. If that happens while you are away from the keyboard, the only visible signal is usually buried in a terminal or transcript file.

Agent Island turns that into a local macOS signal:

- **working**: the provider logo rotates.
- **your turn**: a foreground alarm window opens and keeps ringing until dismissed.
- **needs attention**: limit, login, network, or provider errors turn the affected provider logo red.

The implementation is intentionally conservative. It would rather miss a stale session than flash a false alarm for an old transcript or a slow tool call.

## The Data Source Is Local

There is no shared live-state API for Claude Code and Codex CLI sessions, so Agent Island reads the same local artifacts the tools already write.

Claude Code activity is detected from JSONL transcript files under:

```text
~/.claude/projects/*.jsonl
```

The trigger picker uses Claude Desktop's session store for nicer labels and archived filtering:

```text
~/Library/Application Support/Claude/claude-code-sessions/local_*.json
```

Codex activity is detected from recent JSONL sessions under:

```text
~/.codex/sessions/YYYY/MM/DD/*.jsonl
```

For Codex session labels, Agent Island reads the first `session_meta` JSONL event and uses its `id` and `cwd`.

This keeps state detection local. The app does not need a third-party service just to decide whether a session is still moving.

## The State Model

The scanner runs every six seconds and classifies each candidate transcript. File I/O happens off the main actor so the notch UI stays responsive.

The core inputs are:

- transcript modification time;
- the last few JSONL events;
- whether this app has recently observed the file producing output.

The thresholds live in `Sources/Trigger/SessionScanner.swift`:

| Signal | Current threshold | Meaning |
|---|---:|---|
| `activeWindow` | 18 seconds | Fresh transcript writes mean the session is working. |
| `stallAfter` | 300 seconds | A watched working session that freezes mid-turn for 5 minutes becomes stalled. |
| `stallCap` | 15 minutes | Past this, the app treats it as idle rather than a live stall. |
| `needsYouCap` | 20 minutes | A finished turn is only surfaced as "your turn" while it is still fresh. |
| `attentionWindow` | 30 minutes | Old transcripts are ignored. |

That last point matters. Agent Island only sounds the alarm if it first saw the session working. A transcript that was already old when the app launched should not suddenly become a red alert.

## Detecting "Your Turn"

Agent Island tails the last 128 KB, capped to 200 lines, so a recent completion marker is unlikely to be pushed out by a burst of tool output.

For Claude Code, the scanner looks for an assistant event whose stop reason indicates the assistant turn is complete:

```text
stop_reason: end_turn
stop_reason: stop_sequence
stop_reason: stop
```

For Codex, the scanner looks for Codex event messages:

```text
payload.type: task_complete
```

If a newer `task_started` appears before a completion marker, Codex is treated as still working.

## Detecting Attention States

A stalled session is not just "a file did not change for a while." That would be too noisy.

The scanner only returns stalled when all of these are true:

- the transcript was recently observed in the working state;
- the transcript has stopped changing for at least five minutes;
- no fresh turn-complete marker is visible in the tail;
- the session is still inside the 15-minute stall cap.

This catches the failure mode Agent Island cares about: a long-running agent that was active, then froze mid-turn while you were not watching. Usage fetch errors can also promote a provider into an attention state when credentials expire, provider checks are temporarily refused, or another recoverable service error appears.

## Aggregating Per Provider

Claude and Codex can each have multiple candidate transcripts. Agent Island classifies each file, then takes the most urgent state for that provider:

```text
idle < working < your turn < stalled < rate limited < auth required
```

That gives one visible Claude state and one visible Codex state in the notch.

The scanner also prunes `lastWorking` entries to only files still considered candidates, so long-running app sessions do not leak unbounded path state.

## Why This Belongs In The Notch

The implementation is not trying to replace a terminal UI, dashboard, or session browser. Those are useful when you want detail.

The notch is for a narrower job: a persistent, low-friction signal that answers one question while you work on something else:

> Is my agent still moving, waiting for me, or stuck?

That is the surface Agent Island optimizes for.
