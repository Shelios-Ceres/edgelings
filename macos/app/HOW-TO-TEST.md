# Edgelings for macOS: how to test it

Thank you for testing. This is an early build of Edgelings, the little characters that sit on the edge of your screen and show what your AI coding agents are doing.

## Install

1. Unzip `Edgelings-macos-test.zip` (the zip is inside the download). You get `Edgelings.app`.
2. **Move** it to `/Applications`: drag it there, or run `mv Edgelings.app /Applications/`. Do not copy it: a second copy left in the unzipped folder shows up as a second "Edgelings" in Spotlight. If you already have two, find them with

   ```sh
   mdfind "kMDItemCFBundleIdentifier == 'dev.edgelings.Edgelings'"
   ```

   and delete every copy except the one in `/Applications`.
3. Open Terminal and run these lines, once:

   ```sh
   xattr -dr com.apple.quarantine /Applications/Edgelings.app
   touch /Applications/Edgelings.app
   open /Applications/Edgelings.app
   ```

   The first line removes the "downloaded from the internet" mark. The build is not signed by Apple, so macOS would otherwise refuse to open it. If you skip it, right-click the app, choose Open, and confirm. The second line makes Finder and Spotlight re-read the app's icon (a purple robot head on a cream square).

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

This download has four files:

- `HOW-TO-TEST.md` (this one);
- `CHECKLIST.md`: two minutes of looking and clicking, for a person (the drawer, hover, click, right-click, drag, the Settings pages, dark mode, and the CPU in Activity Monitor);
- `PROBE.md`: a few read-only Terminal commands that show names, counts and times only. They answer where Kimi Code and Codex show up on your Mac. An agent can run them for you;
- `Edgelings-macos-test.zip`.

Then open **Settings > General > About > Copy diagnostic** (or run `/Applications/Edgelings.app/Contents/MacOS/Edgelings --diagnose` in Terminal). It lists ids, counts and states only: which agent folders exist, how many files changed in the last day, the names of the programs it counted as each agent (and who started them), the power source, the screen's scale and the frame rate. It never shows window titles, session names, folders or message text. Paste it in your reply, together with the checklist answers and a screenshot if you can.

The app also keeps a log at `~/Library/Application Support/Edgelings/crash.log`. It holds error names and file names of the app itself, never your conversations.
