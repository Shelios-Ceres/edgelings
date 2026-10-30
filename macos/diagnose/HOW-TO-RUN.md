# Edgelings diagnostic: how to run it

This small tool shows what the Edgelings core sees on your Mac: which AI coding agents are running, how many sessions each has and what they are doing, which logins exist, and token totals. It is a first check that the Mac port reads real state correctly.

**It prints ids, counts and states only.** It never prints window titles, session names, folder names, e-mail addresses, tokens or any message text. It writes nothing outside a temporary folder it deletes at the end. It sends nothing anywhere, unless you add `--fetch` (below).

## Run it

1. Unzip the download. You get a folder with two files: `edgelings-diagnose` and `AgentIslandBridge.dylib`. Keep them together.
2. Open Terminal, and run these two lines (drag the folder onto the Terminal window after `cd ` to fill in its path):

   ```sh
   cd <the folder>
   chmod +x edgelings-diagnose && xattr -dr com.apple.quarantine . && ./edgelings-diagnose | tee edgelings-report.txt
   ```

   `chmod +x` gives the tool back its "may run" flag (the download drops it). `xattr` removes the "downloaded from the internet" mark that would otherwise stop macOS from opening an unsigned tool.
3. Send `edgelings-report.txt` back. Read it first if you like: every line is a name of an agent, a yes/no, or a number.

If macOS asks whether `security` may access "Claude Code-credentials" in your keychain, that is Edgelings checking that your Claude login exists; "Allow" is fine, "Always Allow" avoids asking again. The tool never prints what it reads.

## What a good result looks like

For an agent you have open (Claude, Codex, Cursor, Antigravity, Grok, Kimi, DeepSeek):

- `presence` should be `open` with the app in front, `minimized` after you minimize every window, `hidden` when it only lives in the menu bar, and `closed` when you quit it. Try it by minimizing and quitting one app, and running the tool again.
- `process` should be `yes` while it runs.
- `sessions` counts the conversations Core found. Start a long answer in one, run the tool, and it should show a `working` phase.
- Claude's login should say `keychain`, with your plan (for example `max`).

Please tell me about anything that looks wrong: an app that is open but shown `closed`, a count of 0 for an agent with sessions, `windows reported` of 0.

## Optional: quota numbers

```sh
./edgelings-diagnose --fetch
```

This also asks Claude's and Codex's usage servers for your quota windows (percent used and minutes to reset). It uses your stored Claude login the way the app would, and can refresh it (the new token is written back to the same keychain item, as Claude Code expects). Skip it if you would rather not.

## Other options

- `--json` prints the same facts as JSON.
- `--lib <path>` points at the `.dylib` if you keep it elsewhere.
