// "Copy diagnostic" and `Edgelings --diagnose`: what Core sees on this Mac, as
// ids, counts and states only. No window titles, session names, folders,
// e-mail addresses or tokens.
import AppKit

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
        let agents = report["agents"] as? [String: Any] ?? [:]
        for name in agents.keys.sorted() {
            let agent = agents[name] as? [String: Any] ?? [:]
            let phases = (agent["phases"] as? [String: Any] ?? [:]).keys.sorted().map { "\($0) \(value((agent["phases"] as? [String: Any])?[$0]))" }.joined(separator: ", ")
            lines.append("  \(name): presence \(value(agent["presence"])), process \(value(agent["processRunning"])), sessions \(value(agent["sessions"]))\(phases.isEmpty ? "" : " (\(phases))")")
        }
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
        let table = bridge.request("tr", ["keys": UIStrings.keys, "english": true])["strings"] as? [String: String]
        check("strings", (table?.count ?? 0) == UIStrings.keys.count, "\(table?.count ?? 0) of \(UIStrings.keys.count)")
        for theme in ["pixel", "classic"] {
            do {
                let engine = try CharacterEngine(theme: theme, directory: AppController.enginesDirectory())
                let index = engine.addBot(kind: "claude", tempo: 1, seed: 7, look: "")
                engine.setState(index, "thinking", phase: 0)
                let frames = engine.tick([0.1], size: 44)
                check("engine \(theme)", frames.first??.svg.hasPrefix("<svg") == true)
            } catch {
                check("engine \(theme)", false, "\(error)")
            }
        }
        exit(failed ? 1 : 0)
    }
}
