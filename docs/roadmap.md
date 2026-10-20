# Agent Island Roadmap

Agent Island does one job first: keep your agents' status visible, then bring you back when a run needs input. Website: [agent-island.dev](https://agent-island.dev).

## Shipping now (2.1.2)

- Five providers on one island: Claude Code, Claude Desktop, Codex, Antigravity, Grok, and Cursor — live session state for every one of them.
- One app, native on both platforms: SwiftUI on macOS, WinUI 3 on Windows ([windows/](../windows/)), released from the same tag.
- Turn alarms when a background run finishes, plus an out-of-quota alarm with the reset time.
- Live provider mark states: working, idle, your turn, needs attention.
- Usage, reset windows, and a local cost ledger; Codex reset cards when you have any banked.
- Weekly and monthly report cards — tokens, estimated API value, model mix — rendered locally and shared only when you choose to.
- Visual modes with glow-colour choices, a solo layout for single-subscription machines, and a floating-bar position on Windows.
- English and Simplified Chinese.
- Notch-style and compact top-bar layouts, so notched and non-notched Macs both work.

## Near term

- Keep tightening the turn alarm and deep-link flow across every supported agent.
- Better onboarding for first-time users: permissions, login state, and "why am I seeing this state?".
- Clearer public examples for non-notched Macs, external displays, iMac, Mac mini, and older MacBooks.
- Better release notes, issue templates, and contributor docs so bugs are easier to reproduce.

## Planned later

- More provider integrations, once the current five are reliable enough for daily use.
- More install paths if the project gets enough demand.

## Non-goals for now

- A large agent dashboard.
- iOS support.
- Cloud sync or telemetry.
- Running user code on a remote server.

Everything stays local-first and understandable: Agent Island reads local session files, shows local state, and uses the credentials already on your machine.
