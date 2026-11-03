// "Copy diagnostic" and `Edgelings --diagnose`: what Core sees on this Mac, as
// ids, counts and states only. No window titles, session names, folders,
// e-mail addresses or tokens.
import AppKit
import CoreText

enum DiagnosticReport {
    static func text(from report: [String: Any]) -> String {
        func value(_ item: Any?) -> String {
            switch item {
            case let number as NSNumber:
                if CFGetTypeID(number) == CFBooleanGetTypeID() { return number.boolValue ? "yes" : "no" }
                return number.stringValue
            case let string as String: return string
            default: return "-"
            }
        }
        var lines = ["Edgelings diagnostic (ids, counts and states only)"]
        if let error = report["error"] { return lines.joined(separator: "\n") + "\nCore answered with an error: \(value(error))" }
        lines.append("System: \(value(report["os"])), \(value(report["arch"]))")
        lines.append("SQLite: \(value(report["sqlite"]))")
        lines.append("Windows reported by Swift: \(value(report["windowsReported"])) app(s)")
        lines.append(contentsOf: systemLines())
        let agents = report["agents"] as? [String: Any] ?? [:]
        for name in agents.keys.sorted() {
            let agent = agents[name] as? [String: Any] ?? [:]
            let phases = (agent["phases"] as? [String: Any] ?? [:]).keys.sorted().map { "\($0) \(value((agent["phases"] as? [String: Any])?[$0]))" }.joined(separator: ", ")
            lines.append("  \(name): presence \(value(agent["presence"])), process \(value(agent["processRunning"])), sessions \(value(agent["sessions"]))\(phases.isEmpty ? "" : " (\(phases))")")
            let folders = agent["folders"] as? [String: Any] ?? [:]
            for label in folders.keys.sorted() {
                let folder = folders[label] as? [String: Any] ?? [:]
                var detail = "\(value(folder["exists"]))"
                if let changed = folder["changedIn24h"] { detail += ", changed in the last 24 h: \(value(changed))" }
                if let changed = folder["filesChangedIn24h"] {
                    detail += ", \(value(changed)) of \(value(folder["filesSeen"])) files changed in the last 24 h\(folder["capped"] != nil ? " (capped)" : "")"
                }
                lines.append("      \(label): \(detail)")
            }
            let matched = agent["matchedProcesses"] as? [[String: Any]] ?? []
            for process in matched {
                lines.append("      process \(value(process["name"])) x\(value(process["count"])) by \(value(process["rule"])), parent \(value(process["parent"]))")
            }
        }
        if let ledger = report["cursorLedger"] as? [String: Any] {
            lines.append("Cursor ledger: \(value(ledger["bubblesWithTokenCount"])) bubbles with a token count, \(value(ledger["nonZero"])) non-zero, \(value(ledger["nonZeroDated"])) of those dated")
        }
        if let folders = report["kimiSupportFolders"] as? [String] {
            lines.append("Application Support folders named kimi or moonshot: \(folders.isEmpty ? "none" : folders.joined(separator: ", "))")
        }
        lines.append(contentsOf: profileLines(report))
        let credentials = report["credentials"] as? [String: Any] ?? [:]
        lines.append("Claude login: \(value(credentials["claudeLoginSource"])) plan \(value(credentials["claudePlan"]))")
        lines.append("Codex auth file: \(value(credentials["codexAuthFile"])), Grok auth file: \(value(credentials["grokAuthFile"])), Cursor database: \(value(credentials["cursorDatabase"]))")
        let tokens = report["tokens30d"] as? [String: Any] ?? [:]
        for name in tokens.keys.sorted() {
            let entry = tokens[name] as? [String: Any] ?? [:]
            lines.append("  tokens 30d \(name): \(value(entry["events"])) events, \(value(entry["wireTokens"])) tokens, \(value(entry["models"])) models")
        }
        return lines.joined(separator: "\n")
    }

    /// Where the CPU goes: the process's share, then each subsystem's milliseconds per minute of the app's
    /// life and what share of one core that is (60,000 ms per minute is one whole core). Names and times only.
    static func profileLines(_ report: [String: Any]) -> [String] {
        let shares = Perf.cpuShares()
        var lines = ["CPU: \(shares.lastMinute.map { String(format: "%.1f%%", $0) } ?? "not sampled yet") of one core over the last minute, \(String(format: "%.1f%%", shares.sinceStart)) since start (up \(Int(shares.uptime)) s)"]
        var rows: [(String, Int, Double, Double)] = Perf.rows().map { ("app: " + $0.name, $0.count, $0.msPerMinute, $0.worstMs) }
        for row in (report["profile"] as? [[String: Any]]) ?? [] {
            rows.append(("core: " + (row["name"] as? String ?? "?"), (row["count"] as? NSNumber)?.intValue ?? 0, (row["msPerMinute"] as? NSNumber)?.doubleValue ?? 0, (row["worstMs"] as? NSNumber)?.doubleValue ?? 0))
        }
        lines.append("Where the time goes (ms per minute of the app's life, share of one core, runs, slowest run):")
        for row in rows.sorted(by: { $0.2 > $1.2 }).prefix(28) {
            let name = row.0.padding(toLength: 40, withPad: " ", startingAt: 0)
            lines.append(String(format: "  %@ %8.0f ms/min  %5.1f%%  %6d runs  worst %7.1f ms", name, row.2, row.2 / 600, row.1, row.3))
        }
        return lines
    }

    /// What only Swift knows: the displays' scale, the power source, and the loop's settings and rate.
    static func systemLines() -> [String] {
        var lines: [String] = []
        let screens = NSScreen.screens
        lines.append("Displays: \(screens.count), backing scale \(screens.map { String(format: "%.0fx", $0.backingScaleFactor) }.joined(separator: ", "))")
        lines.append("Power: \(Power.source() ?? "unknown"), Low Power Mode \(Power.lowPowerMode ? "yes" : "no"), saver counts it as battery: \(Power.onBattery ? "yes" : "no")")
        lines.append("Reduce Motion: \(NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? "yes" : "no")")
        if let frame = AppController.shared.frame {
            let rate = frame.fps == 0 ? "match the display" : "\(frame.fps) fps"
            let pace = DrawerPlacementHelper.drawer?.pace ?? 0
            lines.append("Drawer: \(frame.theme) style, \(frame.shownBots.count) of \(frame.bots.count) characters shown, frame rate setting \(rate), Save battery \(frame.saveBattery ? "on" : "off"), loop now \(pace == 0 ? "resting" : String(format: "%.0f fps", pace))")
        }
        return lines
    }

    /// Copies the report to the pasteboard.
    static func copy() {
        AppController.shared.request("windows", WindowSnapshot.request()) { _ in
            AppController.shared.request("diagnose", [:]) { report in
                let pasteboard = NSPasteboard.general
                pasteboard.clearContents()
                pasteboard.setString(text(from: report), forType: .string)
            }
        }
    }

    /// The report as a file (see EDGELINGS_DIAG_FILE in App.swift).
    static func write(to path: String) {
        AppController.shared.request("windows", WindowSnapshot.request()) { _ in
            AppController.shared.request("diagnose", [:]) { report in
                try? text(from: report).write(toFile: path, atomically: true, encoding: .utf8)
            }
        }
    }

    /// `Edgelings --diagnose`: prints the same text and exits.
    static func printAndExit() -> Never {
        guard let bridge = AppController.shared.bridge else {
            print("Could not load the Core library.")
            exit(1)
        }
        bridge.request("windows", WindowSnapshot.request())
        print(text(from: bridge.request("diagnose", [:])))
        exit(0)
    }
}

/// `Edgelings --self-test`: asks Core for everything the UI reads and decodes
/// it into the Swift models, so a shape that drifted shows up here and not as
/// a silently empty window. Prints names and counts only.
enum SelfTest {
    static func run() -> Never {
        guard let bridge = AppController.shared.bridge else {
            print("FAIL core library did not load")
            exit(1)
        }
        var failed = false
        func check(_ name: String, _ ok: Bool, _ detail: String = "") {
            print("\(ok ? "ok  " : "FAIL") \(name)\(detail.isEmpty ? "" : " (\(detail))")")
            if !ok { failed = true }
        }
        bridge.request("windows", WindowSnapshot.request())
        check("appStart", bridge.request("appStart")["ok"] as? Bool == true)
        let frame = FrameModel.from(bridge.request("frame", ["now": 9.0, "shown": true]))
        check("frame decodes", frame != nil, "\(frame?.bots.count ?? 0) characters, \(frame?.shownBots.count ?? 0) shown")
        let cards = CardsModel.from(bridge.request("card", [:]))
        check("cards decode", cards != nil, "\(cards?.cards.count ?? 0) sections")
        let settings = SettingsModel.from(bridge.request("settings", ["english": true]))
        check("settings decode", settings != nil, "\(settings?.agents.count ?? 0) agents")
        let usage = UsageModel.from(bridge.request("usage", ["english": true]))
        check("usage decode", usage != nil, "\(usage?.agents.count ?? 0) agents")
        let looks = LooksModel.from(bridge.request("looks", ["id": "claude", "theme": "pixel"]))
        check("looks decode", (looks?.looks.count ?? 0) > 0)
        for theme in ["pixel", "classic"] {
            let reply = bridge.request("looks", ["id": "antigravity", "theme": theme, "english": theme == "pixel"])
            check("looks in the \(theme) set (english flag)", (LooksModel.from(reply)?.looks.count ?? 0) > 1, (reply["error"] as? String) ?? "")
        }
        let table = bridge.request("tr", ["keys": UIStrings.keys, "english": true])["strings"] as? [String: String]
        check("strings", (table?.count ?? 0) == Set(UIStrings.keys).count, "\(table?.count ?? 0) of \(Set(UIStrings.keys).count)")
        // Chinese under Pixel: the face loads and draws the app's hard characters, and the line-break rules hold.
        PixelFace.register()
        PixelFace.chinese = true
        check("Chinese pixel face loads", PixelFace.chineseFamily != nil, PixelFace.chineseFamily ?? "missing")
        let probe = Array("额度缓存鲸即垫拖斜概热缘警餐".utf16)
        var glyphs = [CGGlyph](repeating: 0, count: probe.count)
        let drawn = CTFontGetGlyphsForCharacters(CTFontCreateWithName((PixelFace.chineseFamily ?? "") as CFString, 10, nil), probe, &glyphs, probe.count)
        check("Chinese pixel face draws 额度缓存鲸 and the characters Ark 12px lacks", drawn)
        PixelFace.chinese = false
        for (text, allowed, forbidden, name) in PixelBreaks.samples {
            let found = PixelBreaks.opportunities(text)
            check("line breaks: \(name)", allowed.allSatisfy { found.contains($0) } && forbidden.allSatisfy { !found.contains($0) }, "\(found)")
        }
        for sample in FullScreenWatch.samples {
            check("full-screen rule: \(sample.name)", FullScreenWatch.covers(sample.window, display: sample.display) == sample.covers)
        }
        for sample in PixelBreaks.lines {
            let end = PixelBreaks.lineEnd(start: 0, suggested: sample.room, length: (sample.text as NSString).length, opportunities: PixelBreaks.hasWide(sample.text) ? PixelBreaks.opportunities(sample.text) : nil,
                                          phrases: PixelBreaks.phraseEnds(sample.text), text: sample.text as NSString)
            check("line ends: \(sample.name)", end == sample.end, "\(end)")
        }
        for theme in ["pixel", "classic"] {
            do {
                let engine = try CharacterEngine(theme: theme, directory: AppController.enginesDirectory())
                let index = engine.addBot(kind: "claude", tempo: 1, seed: 7, look: "")
                engine.setState(index, "thinking", phase: 0)
                let frames = engine.tick([0.1], size: 44)
                check("engine \(theme)", frames.first.flatMap { $0 }?.svg.hasPrefix("<svg") == true)
                // The work-state rotation: cycle lengths read from the engine, Core's scheduler on a fake clock.
                let look = SampleLooks.look("claude", theme == "pixel" ? .pixel : .classic).id
                let states = (bridge.request("rotationStates", ["theme": theme, "kind": "claude", "look": look])["states"] as? [String]) ?? []
                let cycles = engine.cycles(kind: "claude", look: look, states: states, tempo: 1)
                check("rotation states and cycles (\(theme))", !states.isEmpty && cycles.count == states.count, "\(states.count) states, \(cycles.count) cycles")
                bridge.request("rotationSetup", ["theme": theme, "bots": [["id": "claude", "kind": "claude", "look": look, "cycles": cycles] as [String: Any]]])
                var seen: Set<String> = []
                var grouped = false
                for second in stride(from: 0.0, through: 60.0, by: 1.0) {
                    let reply = bridge.request("rotate", ["now": second, "reduced": false, "bots": [["id": "claude", "phase": "Thinking"]]])
                    let answer = (reply["bots"] as? [[String: Any]])?.first
                    grouped = answer?["grouped"] as? Bool == true
                    if let state = answer?["state"] as? String { seen.insert(state) }
                }
                check("rotation turns Thinking between two motions (\(theme))", grouped && seen.count >= 2, "\(seen.sorted())")
            } catch {
                check("engine \(theme)", false, "\(error)")
            }
        }
        exit(failed ? 1 : 0)
    }
}
