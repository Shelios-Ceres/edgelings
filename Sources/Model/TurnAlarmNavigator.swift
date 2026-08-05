import AppKit
import UserNotifications
import Foundation

@MainActor
enum TurnAlarmNavigator {
    static func open(provider: AlertEngine.Provider, thread: ActivityMonitor.ActiveThread?) {
        switch provider {
        case .codex:
            openCodex(thread: thread)
        case .claude:
            openClaude(thread: thread)
        case .grok:
            // Verified against grok --help (2026-08-08): no per-id resume,
            // only `-c/--continue` = "the most recent session for the
            // current working directory". The command runs in the thread's
            // cwd, where the just-finished session IS the most recent one.
            if let thread, openCLIResume(
                executable: "grok",
                arguments: ["--continue"],
                thread: thread,
                fallbackBundleID: nil
            ) { return }
            // No grok CLI on PATH: the click used to do nothing at all.
            // Fronting the desktop app is the honest floor — the same thing
            // Claude falls back to when its CLI is missing.
            bringForward(appNamed: "Grok")
        case .antigravity:
            // The owner's real flow: agy stays open in its terminal, the
            // turn ends, the alarm fires. Spawning a fresh terminal running
            // `agy --conversation <id>` — the generic resume gesture — put a
            // SECOND copy of a session that was already on screen one window
            // over (owner repro, 2026-08-09). So a running interactive agy
            // gets its own terminal fronted first; the new-terminal resume
            // is the fallback for when nothing is running.
            if let thread, frontRunningAntigravity(conversationId: thread.sessionId) { return }
            // Verified against the real binary (agy 1.1.11, 2026-08-08): the
            // executable is `agy`, there is no --resume, and --conversation
            // takes the conversation id — which is exactly the brain/<id>
            // directory the scanner reports. Confirmed to append to the same
            // transcript rather than start a new thread.
            if let thread, openCLIResume(
                executable: "agy",
                arguments: ["--conversation", thread.sessionId],
                thread: thread,
                fallbackBundleID: nil
            ) { return }
            bringForward(appNamed: "Antigravity")
        case .cursor:
            // Cursor is app-only: no CLI, and no documented route that opens
            // one conversation, so fronting the editor is the whole gesture.
            bringForward(bundleIdentifier: "com.todesktop.230313mzl4w4u92")
        }
    }

    private static let codexBundleID = "com.openai.codex"

    private static func openCodex(thread: ActivityMonitor.ActiveThread?) {
        // The running form wins (owner rule, 2026-08-09): a session live in
        // a terminal fronts that terminal, GUI sessions get the GUI. Unique
        // match only — with two codex processes in one repo the desktop
        // deep link is the safer landing, since it reaches the exact
        // thread and a fronted terminal would be a coin flip.
        if let thread, frontUniqueRunningCLI(executable: "codex", cwd: thread.cwd) { return }
        // Opt-in: CLI people can have their threads reopen in a terminal.
        if CodexJumpPreferenceStore.shared.prefersCLI {
            codexCLIFallback(thread: thread)
            return
        }
        if let id = sanitizedCodexThreadID(thread?.sessionId),
           let url = URL(string: "codex://threads/\(id)"),
           let appURL = appURL(forScheme: url, bundleID: codexBundleID) {
            let config = NSWorkspace.OpenConfiguration()
            config.activates = true
            // Deliver the deep link to the *specific* Codex Desktop we resolved,
            // not whatever LaunchServices treats as the codex:// default. The
            // scheme is claimed by a pile of stale duplicates (old Codex
            // Framework helper copies, Sparkle Updater.app launchers, the
            // computer-use helper) — routing through the default handler can
            // wake one of those instead of the window the user is looking at.
            NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: config) { app, error in
                Task { @MainActor in
                    if let app {
                        activate(app)
                    } else if error != nil {
                        codexCLIFallback(thread: thread)
                    } else {
                        bringForward(bundleIdentifier: codexBundleID)
                    }
                }
            }
            return
        }
        codexCLIFallback(thread: thread)
    }

    private static func codexCLIFallback(thread: ActivityMonitor.ActiveThread?) {
        if let thread, openCLIResume(
            executable: "codex",
            arguments: ["resume", thread.sessionId],
            thread: thread,
            fallbackBundleID: codexBundleID
        ) {
            return
        }
        bringForward(bundleIdentifier: codexBundleID)
    }

    /// The app bundle a scheme URL should be delivered to. Prefer the running
    /// instance of the expected bundle id (so the URL lands in the window that's
    /// actually open) before falling back to LaunchServices' default handler
    /// and finally a by-id lookup.
    private static func appURL(forScheme url: URL, bundleID: String) -> URL? {
        if let running = NSWorkspace.shared.runningApplications
            .first(where: { $0.bundleIdentifier == bundleID })?.bundleURL {
            return running
        }
        if let byScheme = NSWorkspace.shared.urlForApplication(toOpen: url) {
            return byScheme
        }
        return NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID)
    }

    private static func sanitizedCodexThreadID(_ raw: String?) -> String? {
        guard let raw, !raw.isEmpty else { return nil }
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_")
        guard raw.unicodeScalars.allSatisfy({ allowed.contains($0) }) else { return nil }
        return raw
    }

    private static let claudeBundleID = "com.anthropic.claudefordesktop"

    private static func openClaude(thread: ActivityMonitor.ActiveThread?) {
        // Split by where the session actually lives:
        //
        // - Desktop sessions: bring Claude Desktop to the front, nothing more.
        //   There is NO deep link that lands on an existing conversation —
        //   `claude://resume?sessionId=…` is not a real endpoint (Desktop only
        //   registers `claude://…/new`, the CLI registers `claude-cli://open`,
        //   both of which start a NEW session). 1.5.1 briefly resumed these in
        //   a Terminal via `claude --resume`, which does continue the thread —
        //   but a Desktop user clicking "Open thread" expects their Desktop
        //   window, and a Terminal popping up reads as a wrong jump.
        //
        // - CLI sessions: resume for real, the way the auto-trigger engine
        //   does — `claude --resume <id>` in a terminal from the session's
        //   own cwd. That user lives in a terminal already.
        if thread?.launchTarget == .claudeDesktop {
            // Opt-in: some people prefer the terminal's exact-conversation
            // resume over landing in the Desktop app.
            if ClaudeJumpPreferenceStore.shared.prefersCLI,
               let thread, openCLIResume(
                   executable: "claude",
                   arguments: ["--resume", thread.sessionId],
                   thread: thread,
                   fallbackBundleID: claudeBundleID
               ) {
                return
            }
            // Desktop path. Claude registers claude://code/<bridge-id>, a
            // route straight to the conversation — currently behind
            // Anthropic's server-side flag ("code session deep link gated
            // off" in their log), where it merely fronts the app. Firing it
            // costs nothing today and upgrades to a true jump the moment
            // they enable the flag. Clipboard assist stays either way.
            if let thread,
               let bridge = bridgeSessionId(forCLISession: thread.sessionId),
               let url = URL(string: "claude://code/\(bridge)"),
               let appURL = appURL(forScheme: url, bundleID: claudeBundleID) {
                let config = NSWorkspace.OpenConfiguration()
                config.activates = true
                NSWorkspace.shared.open([url], withApplicationAt: appURL, configuration: config) { app, _ in
                    Task { @MainActor in
                        if let app {
                            activate(app)
                        } else {
                            bringForward(bundleIdentifier: claudeBundleID)
                        }
                    }
                }
            } else {
                bringForward(bundleIdentifier: claudeBundleID)
            }
            // Put the session's title on the clipboard and say so — until the
            // deep link is unlocked, finding the conversation is one paste in
            // Claude's search instead of a scroll hunt through the sidebar.
            if let label = thread?.label, !label.isEmpty, label != L10n.tr("Demo thread") {
                let pasteboard = NSPasteboard.general
                pasteboard.clearContents()
                pasteboard.setString(label, forType: .string)
                postClipboardHint(label: label)
            }
            return
        }
        if let thread, openCLIResume(
            executable: "claude",
            arguments: ["--resume", thread.sessionId],
            thread: thread,
            fallbackBundleID: claudeBundleID
        ) {
            return
        }
        bringForward(bundleIdentifier: claudeBundleID)
    }

    /// Claude Desktop's session store keeps, per conversation, the internal
    /// bridge ids (`session_…`/`cse_…`) its own `claude://code/<id>` route
    /// expects. Looked up on click — 60-odd small JSON files, milliseconds —
    /// so the value is always fresh and no scanner plumbing is needed.
    private static func bridgeSessionId(forCLISession sessionId: String) -> String? {
        let root = NSHomeDirectory() + "/Library/Application Support/Claude/claude-code-sessions"
        guard let enumerator = FileManager.default.enumerator(atPath: root) else { return nil }
        for case let rel as String in enumerator
        where rel.hasSuffix(".json") && (rel as NSString).lastPathComponent.hasPrefix("local_") {
            guard let data = FileManager.default.contents(atPath: root + "/" + rel),
                  let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  object["cliSessionId"] as? String == sessionId,
                  let bridges = object["bridgeSessionIds"] as? [String],
                  let bridge = bridges.last(where: { $0.hasPrefix("session_") || $0.hasPrefix("cse_") })
            else { continue }
            return bridge
        }
        return nil
    }

    /// Until Anthropic unlocks the conversation deep link, fronting the app
    /// is as far as it goes — so we surface a quiet notification explaining
    /// the clipboard assist.
    private static func postClipboardHint(label: String) {
        let content = UNMutableNotificationContent()
        content.title = L10n.tr("Session name copied")
        content.body = L10n.tr("Paste it in Claude's search to jump to “%@”.", label)
        let request = UNNotificationRequest(
            identifier: "agent-island-claude-clipboard-hint",
            content: content,
            trigger: nil
        )
        UNUserNotificationCenter.current().add(request, withCompletionHandler: nil)
    }

    private static func activate(_ application: NSRunningApplication) {
        guard let url = application.bundleURL else { return }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        NSWorkspace.shared.openApplication(at: url, configuration: config)
    }

    /// Brings another app to the front from an .accessory app whose key
    /// window is closing in the same gesture. `NSRunningApplication.activate`
    /// alone loses that race on macOS 14+: our window closes a beat later and
    /// AppKit hands focus back to OUR next window — which is why clicking a
    /// Cursor alarm surfaced Agent Island's Settings instead (owner repro).
    /// `openApplication` is an asynchronous request to the workspace that
    /// survives our window teardown, and hiding ourselves right after removes
    /// us from the running order so focus cannot snap back.
    /// Fronts the terminal (or IDE) that already hosts a live Antigravity
    /// session instead of spawning a duplicate.
    ///
    /// Match order: an agy whose cwd maps to the alarm's conversation in
    /// `cache/last_conversations.json` wins outright. That file is not
    /// written continuously by an interactive session though (verified
    /// 2026-08-09 — a live agy's conversation was absent while a finished
    /// --print run's was present), so when exactly one interactive agy is
    /// running it is taken as the session that raised the alarm. Two or
    /// more with no map match stays ambiguous and falls through to the
    /// new-terminal resume.
    private static func frontRunningAntigravity(conversationId: String) -> Bool {
        let interactive: [(app: pid_t, cwd: String?)] =
            AntigravityLanguageServer.antigravityProcesses().compactMap { pid in
                guard let app = ProcessTree.owningGUIApplication(pid) else {
                    return nil
                }
                return (app, ProcessTree.currentWorkingDirectory(pid))
            }
        guard !interactive.isEmpty else { return false }

        let mapped = interactive.first { session in
            guard let cwd = session.cwd else { return false }
            return antigravityConversation(forWorkspace: cwd) == conversationId
        }
        guard let target = mapped ?? (interactive.count == 1 ? interactive[0] : nil) else {
            return false
        }
        return front(applicationPID: target.app)
    }

    /// `cache/last_conversations.json` maps a workspace path to the
    /// conversation last run there.
    private static func antigravityConversation(forWorkspace cwd: String) -> String? {
        for root in SessionScanner.antigravityRoots() {
            let url = URL(fileURLWithPath: root)
                .appendingPathComponent("cache/last_conversations.json")
            guard let data = try? Data(contentsOf: url),
                  let map = try? JSONSerialization.jsonObject(with: data) as? [String: String]
            else { continue }
            if let id = map[cwd] { return id }
            if let id = map[(cwd as NSString).standardizingPath] { return id }
        }
        return nil
    }

    static func bringForward(bundleIdentifier: String) {
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) else {
            activate(bundleIdentifier: bundleIdentifier)
            return
        }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        NSWorkspace.shared.openApplication(at: url, configuration: configuration) { _, _ in
            DispatchQueue.main.async { NSApp.hide(nil) }
        }
    }

    /// Same, addressed by display name for apps whose bundle id we do not
    /// hardcode. No match means the app is not installed — nothing to do.
    static func bringForward(appNamed name: String) {
        let candidates = ["/Applications/\(name).app", "\(NSHomeDirectory())/Applications/\(name).app"]
        guard let path = candidates.first(where: { FileManager.default.fileExists(atPath: $0) }) else { return }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: path), configuration: configuration) { _, _ in
            DispatchQueue.main.async { NSApp.hide(nil) }
        }
    }

    private static func activate(bundleIdentifier: String) {
        if let app = NSWorkspace.shared.runningApplications.first(where: { $0.bundleIdentifier == bundleIdentifier }) {
            activate(app)
            return
        }
        guard let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleIdentifier) else { return }
        NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
    }

    private static func openCLIResume(
        executable: String,
        arguments: [String],
        thread: ActivityMonitor.ActiveThread,
        fallbackBundleID: String?
    ) -> Bool {
        guard !thread.sessionId.isEmpty else { return false }
        // A live interactive session beats any resume spawn: if the CLI
        // that raised this alarm is still running in some terminal, that
        // window IS the destination — spawning a fresh `grok --continue`
        // beside a live grok duplicated the session (user report via the
        // owner, 2026-08-09). Matching is by working directory, which every
        // CLI here scopes its sessions to.
        if frontRunningCLI(executable: executable, cwd: thread.cwd) { return true }
        let command = resumeCommand(executable: executable, arguments: arguments, cwd: thread.cwd)
        let sessionId = thread.sessionId
        // osascript blocks until Terminal handles the Apple Event — on the
        // first run that includes the TCC consent prompt, which can sit for
        // minutes. waitUntilExit on the main actor froze the whole app, so
        // the run happens off-main and the fallbacks hop back for AppKit.
        Task.detached(priority: .userInitiated) {
            // The user's own terminal first (observed, not guessed — see
            // TerminalLauncher); Terminal.app remains the guaranteed floor.
            if await TerminalLauncher.spawnInPreferredTerminal(
                command: command, executable: executable, sessionId: sessionId) { return }
            if runTerminalCommand(command) { return }
            await MainActor.run {
                if openCommandFile(command: command, executable: executable, sessionId: sessionId) { return }
                if let fallbackBundleID { activate(bundleIdentifier: fallbackBundleID) }
            }
        }
        return true
    }

    /// Fronts the terminal (or IDE) hosting a running CLI whose working
    /// directory is the alarm thread's.
    private static func frontRunningCLI(executable: String, cwd: String) -> Bool {
        for appPID in liveCLIHosts(executable: executable, cwd: cwd) {
            if front(applicationPID: appPID) { return true }
        }
        return false
    }

    private static func frontUniqueRunningCLI(executable: String, cwd: String) -> Bool {
        let hosts = liveCLIHosts(executable: executable, cwd: cwd)
        guard hosts.count == 1, let only = hosts.first else { return false }
        return front(applicationPID: only)
    }

    /// GUI apps hosting a live `executable` process whose working directory
    /// is the alarm thread's. Name-exact process match; the agy symlink's
    /// real binary is `antigravity`, so both names count.
    private static func liveCLIHosts(executable: String, cwd: String) -> [pid_t] {
        guard !cwd.isEmpty else { return [] }
        let names: Set<String> = executable == "agy" ? ["agy", "antigravity"] : [executable]
        let wanted = canonicalPath(cwd)
        var hosts: [pid_t] = []
        for pid in ProcessTree.pids(named: names) {
            guard let processCwd = ProcessTree.currentWorkingDirectory(pid),
                  canonicalPath(processCwd) == wanted,
                  let appPID = ProcessTree.owningGUIApplication(pid) else { continue }
            if !hosts.contains(appPID) { hosts.append(appPID) }
        }
        return hosts
    }

    /// The kernel reports cwds symlink-resolved (/private/tmp); scanner
    /// cwds may not be. Canonicalize both sides before comparing.
    private static func canonicalPath(_ path: String) -> String {
        URL(fileURLWithPath: path).resolvingSymlinksInPath().path
    }

    /// Same cooperative-activation route as bringForward: opening an
    /// already-running app activates it reliably on macOS 14+, where plain
    /// activate() can lose the race. Also the observation point that feeds
    /// TerminalLauncher — whatever terminal we front here is the terminal
    /// the user actually lives in.
    private static func front(applicationPID pid: pid_t) -> Bool {
        guard let app = NSRunningApplication(processIdentifier: pid),
              let bundleURL = app.bundleURL else { return false }
        TerminalLauncher.remember(bundleID: app.bundleIdentifier)
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        NSWorkspace.shared.openApplication(at: bundleURL, configuration: configuration) { _, _ in
            DispatchQueue.main.async { NSApp.hide(nil) }
        }
        return true
    }

    nonisolated private static func runTerminalCommand(_ command: String) -> Bool {
        let script = """
        tell application "Terminal"
            activate
            do script "\(appleScriptString(command))"
        end tell
        """
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = ["-e", script]
        process.standardOutput = Pipe()
        process.standardError = Pipe()
        do {
            try process.run()
            process.waitUntilExit()
            return process.terminationStatus == 0
        } catch {
            return false
        }
    }

    private static func openCommandFile(command: String, executable: String, sessionId: String) -> Bool {
        guard let url = TerminalLauncher.writeCommandFile(
            command: command, executable: executable, sessionId: sessionId) else { return false }
        return NSWorkspace.shared.open(url)
    }

    private static func resumeCommand(executable: String, arguments: [String], cwd: String) -> String {
        var parts = [
            "export PATH=\"$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:$PATH\""
        ]
        if isUsableDirectory(cwd) {
            parts.append("cd \(shellQuote(cwd)) || exit 1")
        }
        // nvm/bun/npm-global installs live outside the exported PATH, so
        // resolve through CLILocator. This used to map every non-"codex" name
        // to .claude, which meant clicking a Gemini or Grok alarm launched
        // CLAUDE in the terminal (owner repro, 2026-08-08). Resolve by the
        // actual executable name; an unknown one falls back to PATH lookup.
        let tool = TriggerTool.allCases.first {
            AlertEngine.Provider(rawValue: $0.rawValue)?.cliName == executable
        }
        let binary = tool.flatMap { CLILocator.path(for: $0) } ?? executable
        parts.append("exec \(shellJoin([binary] + arguments))")
        return parts.joined(separator: "; ")
    }

    private static func isUsableDirectory(_ path: String) -> Bool {
        guard !path.isEmpty else { return false }
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory) && isDirectory.boolValue
    }

    private static func shellJoin(_ parts: [String]) -> String {
        parts.map(shellQuote).joined(separator: " ")
    }

    private static func shellQuote(_ raw: String) -> String {
        guard !raw.isEmpty else { return "''" }
        return "'" + raw.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    nonisolated private static func appleScriptString(_ raw: String) -> String {
        raw
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: "\\n")
    }

}
