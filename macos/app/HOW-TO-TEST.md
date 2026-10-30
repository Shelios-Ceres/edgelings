# Edgelings for macOS: how to test it

Thank you for testing. This is an early build of Edgelings, the little characters that sit on the edge of your screen and show what your AI coding agents are doing.

## Install

1. Unzip `Edgelings-macos-test.zip` (the zip is inside the download). You get `Edgelings.app`.
2. Move it to `/Applications` (or anywhere you like).
3. Open Terminal and run these two lines, once:

   ```sh
   xattr -dr com.apple.quarantine /Applications/Edgelings.app
   open /Applications/Edgelings.app
   ```

   The first line removes the "downloaded from the internet" mark. The build is not signed by Apple, so macOS would otherwise refuse to open it. If you skip it, right-click the app, choose Open, and confirm.

## First-run prompts

- **Keychain.** The first time Edgelings reads your Claude Code login, macOS asks whether `security` may access "Claude Code-credentials". Choose Allow, or Always Allow to stop the question. Edgelings only reads the login to ask Claude for your quota numbers.
- **Launch at login.** Off by default. Switch it on in Settings > General, and macOS may show a "Login Items" notice.
- Nothing else is asked. It does not need Screen Recording, Accessibility or Full Disk Access.

## What to try

1. **The drawer.** A column of round characters appears on the right edge of the main display, one per agent it finds (Claude, Codex, Antigravity, Grok, Kimi Code, DeepSeek). Drag one up or down to move the column. Right-click a character for the menu.
2. **Each agent open, minimized, closed.** With an agent's app open, its character is awake. Minimize its windows: it should fall asleep. Quit the app: it sleeps too. Bring it back and it wakes. Please note any agent that does not follow.
3. **A long answer.** Ask Claude (or another agent) something that takes a minute. Its character should change while it works (thinking, reading, writing...) and say a line when it finishes or needs your approval.
4. **The hover card.** Rest the pointer on a character: a card shows the agent's phase, quota bars and token counts. Check the numbers against the agent's own usage page.
5. **Settings.** Click a character, or use the menu-bar icon > Settings. Try every page, both styles (Pixel and Classic) and both languages.
6. **Light and dark.** Switch macOS between them and look at the cards.

## Sending back what you found

Open **Settings > About > Copy diagnostic** (or run `Edgelings.app/Contents/MacOS/Edgelings --diagnose` in Terminal). It lists ids, counts and states only: no window titles, session names, folders or message text. Paste it in your reply, together with a line on what looked wrong, and a screenshot if you can.

The app also keeps a log at `~/Library/Application Support/Edgelings/crash.log`. It holds error names and file names of the app itself, never your conversations.
