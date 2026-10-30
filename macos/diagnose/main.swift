// edgelings-diagnose: what the shared Core sees on this Mac, for a first live
// check of state detection. It prints ids, counts and states only: no window
// titles, session names, folders, e-mail addresses or tokens. Core keeps its
// caches in a temporary folder, never in the app's own.
//
//   edgelings-diagnose [--lib <AgentIslandBridge.dylib>] [--fetch] [--json]
//
// --fetch also asks Claude's and Codex's usage endpoints for their quota
// windows. That uses the stored Claude login the way the app does, and can
// rotate its refresh token (written back to the same Keychain item).
import Foundation

let arguments = CommandLine.arguments
let wantsJSON = arguments.contains("--json")
let wantsFetch = arguments.contains("--fetch")

func libraryPath() -> String {
    if let at = arguments.firstIndex(of: "--lib"), at + 1 < arguments.count { return arguments[at + 1] }
    let here = URL(fileURLWithPath: arguments[0]).resolvingSymlinksInPath().deletingLastPathComponent()
    return here.appendingPathComponent("AgentIslandBridge.dylib").path
}

guard let bridge = CoreBridge(libraryPath: libraryPath()) else {
    print("Could not load the Core library. Keep edgelings-diagnose and AgentIslandBridge.dylib in one folder.")
    exit(1)
}

let cacheFolder = NSTemporaryDirectory() + "edgelings-diagnose-" + UUID().uuidString
bridge.request("configure", ["cache": cacheFolder])
func finish(_ code: Int32) -> Never {
    try? FileManager.default.removeItem(atPath: cacheFolder)
    exit(code)
}

// Windows first: Core's presence check uses the latest report.
let windows = bridge.request("windows", WindowSnapshot.request())
var request: [String: Any] = [:]
if wantsFetch { request["fetch"] = true }
let report = bridge.request("diagnose", request)

if wantsJSON {
    let data = (try? JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys])) ?? Data()
    print(String(decoding: data, as: UTF8.self))
    finish(report["error"] == nil ? 0 : 1)
}

func text(_ value: Any?) -> String {
    switch value {
    case let number as NSNumber:
        if CFGetTypeID(number) == CFBooleanGetTypeID() { return number.boolValue ? "yes" : "no" }
        return number.stringValue
    case let string as String: return string
    default: return "-"
    }
}

print("Edgelings diagnostic. It prints ids, counts and states only.")
if let error = report["error"] { print("Core answered with an error: \(text(error))"); finish(1) }
print("System: \(text(report["os"])), \(text(report["arch"]))")
print("Windows reported by Swift: \(text(windows["windows"])) app(s) with an ordinary window")
print("")

let agents = report["agents"] as? [String: Any] ?? [:]
print("Agents (presence = how much of the app is on screen: open, minimized, hidden, closed):")
for name in agents.keys.sorted() {
    let agent = agents[name] as? [String: Any] ?? [:]
    let phases = (agent["phases"] as? [String: Any] ?? [:]).keys.sorted()
        .map { "\($0) \(text((agent["phases"] as? [String: Any])?[$0]))" }.joined(separator: ", ")
    print("  \(name.padding(toLength: 12, withPad: " ", startingAt: 0)) presence \(text(agent["presence"]).padding(toLength: 9, withPad: " ", startingAt: 0))"
        + " process \(text(agent["processRunning"]))   sessions \(text(agent["sessions"]))\(phases.isEmpty ? "" : " (\(phases))")")
}

let credentials = report["credentials"] as? [String: Any] ?? [:]
print("")
print("Logins found (not their contents):")
print("  Claude: \(text(credentials["claudeLoginSource"]))"
    + (credentials["claudePlan"] == nil ? "" : ", plan \(text(credentials["claudePlan"]))")
    + (credentials["claudeAccessTokenMinutesLeft"] == nil ? "" : ", access token valid for \(text(credentials["claudeAccessTokenMinutesLeft"])) more minutes"))
print("  Codex auth file: \(text(credentials["codexAuthFile"]))   Grok auth file: \(text(credentials["grokAuthFile"]))   Cursor database: \(text(credentials["cursorDatabase"]))")

let tokens = report["tokens30d"] as? [String: Any] ?? [:]
print("")
print("Token totals, last 30 days (events / tokens / models):")
for name in tokens.keys.sorted() {
    let entry = tokens[name] as? [String: Any] ?? [:]
    print("  \(name.padding(toLength: 12, withPad: " ", startingAt: 0)) \(text(entry["events"])) / \(text(entry["wireTokens"])) / \(text(entry["models"]))")
}

if let quota = report["quota"] as? [String: Any] {
    print("")
    print("Quota windows (percent used, minutes until reset):")
    for name in quota.keys.sorted() {
        let provider = quota[name] as? [String: Any] ?? [:]
        if let error = provider["error"] { print("  \(name): \(text(error))"); continue }
        for window in ["fiveHour", "weekly"] {
            let w = provider[window] as? [String: Any] ?? [:]
            print("  \(name) \(window): \(text(w["usedPercent"]))%"
                + (w["resetsInMinutes"] == nil ? "" : ", resets in \(text(w["resetsInMinutes"])) min")
                + (w["error"] == nil ? "" : ", note: \(text(w["error"]))"))
        }
    }
} else {
    print("")
    print("Quota windows were not fetched (run with --fetch to ask Claude's and Codex's endpoints).")
}
print("")
print("Done. Nothing was written outside a temporary folder.")
finish(0)
