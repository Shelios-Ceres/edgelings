# Security Policy

## Supported Versions

There are no Edgelings releases yet. Security fixes go into the latest commit on `main`.

## Reporting a Vulnerability

Please report a security issue privately, through GitHub's private vulnerability reporting:

<https://github.com/Shelios-Ceres/edgelings/security/advisories/new>

If that route isn't available, open a GitHub issue with a minimal description and leave out sensitive logs, access tokens, OAuth data and local transcript contents. Private details will only be asked for if they are needed to reproduce the issue.

## Scope

Useful reports include:

- A way for Edgelings to expose an agent's session contents unexpectedly.
- Incorrect handling of local credentials, OAuth refresh state or usage API responses. Edgelings reads each agent's own sign-in file to ask that agent's service for quota, and writes a refreshed token back for Claude and Grok.
- Hooks Edgelings can install (a Notification hook in `~/.claude/settings.json`, a hook file under `~/.grok/hooks`) doing more than observe.
- Packaging or build integrity issues.

Out of scope:

- Vulnerabilities in Claude Code, Codex, Kimi Code, Antigravity, Cursor, Grok, DeepSeek Harness, GitHub, Windows, macOS or other upstream services, unless Edgelings adds risk of its own.
- Reports that require turning off normal operating-system security controls.
- Social engineering, spam, or denial-of-service reports without a concrete Edgelings bug.

## Local Data Boundary

Edgelings runs on your own computer and reads the agents' local session files. Bug reports can still carry sensitive data from terminal sessions, local transcripts or provider tokens, so please remove secrets and private project content before sharing anything. The README's privacy section lists what the Windows app reads, sends and writes.
