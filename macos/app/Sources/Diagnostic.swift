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
