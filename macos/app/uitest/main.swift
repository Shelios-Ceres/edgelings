// edgelings-uitest: drives the real Edgelings.app on a real (runner) screen.
//
//   edgelings-uitest <Edgelings.app> <out-folder> <synthetic-root> [--seconds 60]
//
// It launches the app with EDGELINGS_ROOT on a synthetic home, finds the
// drawer, its hover card, Settings, the menu and the menu-bar item through
// CGWindowListCopyWindowInfo (owner, layer, bounds only: never a title), drives
// them with real CGEvent mouse input, saves a screenshot at each step and the
// window list as JSON, and measures the app's CPU and memory with the bots
// animating, at the default frame rate and with the battery saver. It exits 1
// on a crash, a missing or off-screen drawer or a hover card that never shows.
// It only ever looks at the machine it runs on.
import Foundation
import AppKit
import CoreGraphics

struct Step: Codable {
    var name: String
    var ok: Bool
    var detail: String
    var fatal: Bool
}

struct WindowInfo: Codable {
    var owner: String
    var pid: Int32
    var layer: Int
    var x: Double
    var y: Double
    var width: Double
    var height: Double
    var onScreen: Bool
    var alpha: Double
}

let arguments = CommandLine.arguments
guard arguments.count >= 4 else {
    print("usage: edgelings-uitest <Edgelings.app> <out-folder> <synthetic-root> [--seconds 60]")
    exit(2)
}
let appPath = arguments[1]
let outFolder = arguments[2]
let rootFolder = arguments[3]
let measureSeconds: Int = {
    if let at = arguments.firstIndex(of: "--seconds"), at + 1 < arguments.count, let value = Int(arguments[at + 1]) { return value }
    return 60
}()
try? FileManager.default.createDirectory(atPath: outFolder, withIntermediateDirectories: true)

var steps: [Step] = []
var shots = 0
var eventsBlocked = false

func record(_ name: String, _ ok: Bool, _ detail: String = "", fatal: Bool = false) {
    steps.append(Step(name: name, ok: ok, detail: detail, fatal: fatal))
    print("\(ok ? "ok  " : (fatal ? "FAIL" : "warn")) \(name)\(detail.isEmpty ? "" : " (\(detail))")")
}

func windows() -> [WindowInfo] {
    guard let list = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] else { return [] }
    return list.compactMap { entry in
        guard let owner = entry[kCGWindowOwnerName as String] as? String,
              let ownerPid = (entry[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value,
              let bounds = entry[kCGWindowBounds as String] as? [String: Any],
              let rect = CGRect(dictionaryRepresentation: bounds as CFDictionary) else { return nil }
        return WindowInfo(owner: owner, pid: ownerPid, layer: (entry[kCGWindowLayer as String] as? NSNumber)?.intValue ?? 0,
                          x: rect.minX, y: rect.minY, width: rect.width, height: rect.height,
                          onScreen: (entry[kCGWindowIsOnscreen as String] as? NSNumber)?.boolValue ?? false,
                          alpha: (entry[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1)
    }
}

func screenshot(_ name: String) {
    shots += 1
    let file = String(format: "%@/%02d-%@.png", outFolder, shots, name)
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
    process.arguments = ["-x", file]
    try? process.run()
    process.waitUntilExit()
    let size = (try? FileManager.default.attributesOfItem(atPath: file)[.size] as? Int) ?? 0
    if size == 0 { print("note screenshot \(name): screencapture produced nothing (status \(process.terminationStatus))") }
}

func dumpWindows(_ name: String, pid: Int32) {
    let list = windows().filter { $0.pid == pid }
    if let data = try? JSONEncoder().encode(list) { try? data.write(to: URL(fileURLWithPath: "\(outFolder)/windows-\(name).json")) }
}

func pause(_ seconds: Double) { Thread.sleep(forTimeInterval: seconds) }

// MARK: Input

func post(_ type: CGEventType, _ point: CGPoint, button: CGMouseButton = .left) {
    CGEvent(mouseEventSource: nil, mouseType: type, mouseCursorPosition: point, mouseButton: button)?.post(tap: .cghidEventTap)
}

func moveMouse(to point: CGPoint) {
    // A few steps, as a hand moves, so tracking areas see the pointer arrive.
    let start = CGEvent(source: nil)?.location ?? point
    for step in 1...6 {
        let t = CGFloat(step) / 6
        post(.mouseMoved, CGPoint(x: start.x + (point.x - start.x) * t, y: start.y + (point.y - start.y) * t))
        pause(0.02)
    }
}

func click(_ point: CGPoint, right: Bool = false) {
    moveMouse(to: point)
    pause(0.1)
    let down: CGEventType = right ? .rightMouseDown : .leftMouseDown
    let up: CGEventType = right ? .rightMouseUp : .leftMouseUp
    post(down, point, button: right ? .right : .left)
    pause(0.06)
    post(up, point, button: right ? .right : .left)
}

func drag(from start: CGPoint, to end: CGPoint) {
    moveMouse(to: start)
    pause(0.1)
    post(.leftMouseDown, start)
    for step in 1...12 {
        let t = CGFloat(step) / 12
        post(.leftMouseDragged, CGPoint(x: start.x + (end.x - start.x) * t, y: start.y + (end.y - start.y) * t))
        pause(0.03)
    }
    post(.leftMouseUp, end)
}

func pressEscape() {
    for down in [true, false] {
        let event = CGEvent(keyboardEventSource: nil, virtualKey: 53, keyDown: down)
        event?.post(tap: .cghidEventTap)
    }
}

// MARK: The app

func prepareHome() {
    let home = "\(rootFolder)/home"
    for folder in [".claude/projects", ".codex/sessions", ".kimi-code/sessions", ".dsh/sessions"] {
        try? FileManager.default.createDirectory(atPath: "\(home)/\(folder)", withIntermediateDirectories: true)
    }
}

func launch(extra: [String: String] = [:], log: String) -> Process? {
    let executable = "\(appPath)/Contents/MacOS/Edgelings"
    let process = Process()
    process.executableURL = URL(fileURLWithPath: executable)
    var environment = ProcessInfo.processInfo.environment
    environment["EDGELINGS_ROOT"] = rootFolder
    for (key, value) in extra { environment[key] = value }
    process.environment = environment
    FileManager.default.createFile(atPath: log, contents: nil)
    if let handle = FileHandle(forWritingAtPath: log) { process.standardOutput = handle; process.standardError = handle }
    do { try process.run() } catch {
        record("launch", false, "\(error)", fatal: true)
        return nil
    }
    return process
}

func waitFor(_ seconds: Double, _ condition: () -> Bool) -> Bool {
    let end = Date().addingTimeInterval(seconds)
    while Date() < end {
        if condition() { return true }
        pause(0.25)
    }
    return condition()
}

func drawer(pid: Int32) -> WindowInfo? {
    windows().first { $0.pid == pid && $0.layer == 25 && abs($0.width - 56) <= 2 && $0.onScreen }
}

func menuBarItem(pid: Int32) -> WindowInfo? {
    windows().first { $0.pid == pid && $0.layer == 25 && $0.width < 56 && $0.y < 40 && $0.onScreen }
}

func cardWindow(pid: Int32) -> WindowInfo? {
    windows().first { $0.pid == pid && $0.layer == 26 && $0.width >= 150 && $0.onScreen }
}

func sample(pid: Int32, seconds: Int) -> (cpu: Double, peakCpu: Double, rssMB: Double) {
    var cpu: [Double] = []
    var rss: [Double] = []
    for _ in 0..<(seconds / 2) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/ps")
        process.arguments = ["-o", "%cpu=,rss=", "-p", "\(pid)"]
        let pipe = Pipe()
        process.standardOutput = pipe
        try? process.run()
        process.waitUntilExit()
        let text = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .split(whereSeparator: { $0 == " " || $0 == "\n" }).map(String.init)
        if text.count >= 2, let c = Double(text[0]), let r = Double(text[1]) { cpu.append(c); rss.append(r / 1024) }
        pause(2)
    }
    let average = cpu.isEmpty ? 0 : cpu.reduce(0, +) / Double(cpu.count)
    return (average, cpu.max() ?? 0, rss.max() ?? 0)
}

func appendLine(_ text: String, to path: String) {
    let data = Data((text + "\n").utf8)
    if let handle = FileHandle(forWritingAtPath: path) {
        handle.seekToEndOfFile()
        handle.write(data)
        handle.closeFile()
    } else {
        try? data.write(to: URL(fileURLWithPath: path))
    }
}

// MARK: Run

prepareHome()
let display = CGDisplayBounds(CGMainDisplayID())
print("main display \(Int(display.width))x\(Int(display.height)) at \(Int(display.minX)),\(Int(display.minY))")
guard let app = launch(log: "\(outFolder)/app.log") else { exit(1) }
let pid = app.processIdentifier

let appeared = waitFor(30) { drawer(pid: pid) != nil }
screenshot("launched")
dumpWindows("launched", pid: pid)
guard appeared, let panel = drawer(pid: pid) else {
    record("drawer appears", false, app.isRunning ? "no drawer panel on screen after 30 s" : "the app exited with status \(app.terminationStatus)", fatal: true)
    if let data = try? JSONEncoder().encode(steps) { try? data.write(to: URL(fileURLWithPath: "\(outFolder)/uitest.json")) }
    exit(1)
}
record("drawer appears", true, "panel \(Int(panel.width))x\(Int(panel.height)) at \(Int(panel.x)),\(Int(panel.y))")
let onRightEdge = abs(panel.x + panel.width - display.maxX) <= 2
record("drawer is on the right edge of the main display", onRightEdge, "right edge \(Int(panel.x + panel.width)) of \(Int(display.maxX))", fatal: true)
let inside = panel.y >= display.minY - 1 && panel.y + panel.height <= display.maxY + 1
record("drawer is inside the display", inside, "y \(Int(panel.y))-\(Int(panel.y + panel.height))", fatal: true)
let item = menuBarItem(pid: pid)
record("menu-bar item exists", item != nil, item.map { "at \(Int($0.x)),\(Int($0.y)) \(Int($0.width))x\(Int($0.height))" } ?? "no status-item window found", fatal: false)

// Can this process move the pointer at all?
let probe = CGPoint(x: display.midX, y: display.midY)
moveMouse(to: probe)
pause(0.3)
let seen = CGEvent(source: nil)?.location ?? .zero
eventsBlocked = abs(seen.x - probe.x) > 3 || abs(seen.y - probe.y) > 3
record("CGEvent mouse input moves the pointer", !eventsBlocked, "asked \(Int(probe.x)),\(Int(probe.y)); pointer at \(Int(seen.x)),\(Int(seen.y))")

if !eventsBlocked {
    // Slots: 6 pt in, 12 pt down, 44 pt discs 10 pt apart.
    let first = CGPoint(x: panel.x + 6 + 22, y: panel.y + 12 + 22)

    moveMouse(to: first)
    _ = waitFor(4) { cardWindow(pid: pid) != nil }
    pause(0.4)
    screenshot("hover-card")
    dumpWindows("hover-card", pid: pid)
    if let card = cardWindow(pid: pid) {
        let beside = card.x + card.width <= panel.x + 8
        record("hovering a character opens its card beside it", beside, "card \(Int(card.width))x\(Int(card.height)) at \(Int(card.x)),\(Int(card.y))", fatal: true)
    } else {
        record("hovering a character opens its card beside it", false, "no card window after 4 s", fatal: true)
    }
    moveMouse(to: CGPoint(x: display.midX, y: display.midY))
    pause(0.8)

    // Right-click: the menu.
    click(first, right: true)
    pause(0.6)
    screenshot("right-click-menu")
    let menu = windows().first { $0.pid == pid && $0.layer >= 100 && $0.onScreen }
    record("right-click opens the menu", menu != nil, menu.map { "menu window \(Int($0.width))x\(Int($0.height))" } ?? "no menu window")
    pressEscape()
    pause(0.5)
    moveMouse(to: CGPoint(x: display.midX, y: display.midY))
    pause(0.5)

    // Drag along the edge.
    let before = drawer(pid: pid)
    let room = display.maxY - (panel.y + panel.height) - 20
    let travel: CGFloat = min(120, max(40, room))
    drag(from: first, to: CGPoint(x: first.x, y: first.y + travel))
    pause(0.8)
    screenshot("after-drag")
    if let before, let after = drawer(pid: pid) {
        record("dragging a character moves the drawer along the edge", abs(after.y - before.y) > 10, "y \(Int(before.y)) -> \(Int(after.y))")
        let stays = abs(after.x - before.x) < 2 && after.y >= display.minY - 1 && after.y + after.height <= display.maxY + 1
        record("the drawer stays on the edge and inside the display", stays, "x \(Int(after.x)), y \(Int(after.y))", fatal: true)
    } else {
        record("dragging a character moves the drawer along the edge", false, "the drawer vanished", fatal: true)
    }
    moveMouse(to: CGPoint(x: display.midX, y: display.midY))
    pause(0.6)

    // Menu-bar item: left click shows the all-agents card, right click the menu.
    if let item = menuBarItem(pid: pid) {
        let spot = CGPoint(x: item.x + item.width / 2, y: item.y + item.height / 2)
        click(spot)
        pause(1.0)
        screenshot("menu-bar-click")
        record("clicking the menu-bar icon shows the all-agents card", cardWindow(pid: pid) != nil)
        pause(5.5)
        click(spot, right: true)
        pause(0.8)
        screenshot("menu-bar-right-click")
        let barMenu = windows().first { $0.pid == pid && $0.layer >= 100 && $0.onScreen }
        record("right-clicking the menu-bar icon opens its menu", barMenu != nil)
        pressEscape()
        pause(0.5)
    }

    // Click a character: Settings.
    let current = drawer(pid: pid) ?? panel
    click(CGPoint(x: current.x + 28, y: current.y + 34))
    let settings = waitFor(6) { windows().contains { $0.pid == pid && $0.layer == 0 && $0.width >= 500 && $0.onScreen } }
    pause(0.8)
    screenshot("settings")
    dumpWindows("settings", pid: pid)
    record("clicking a character opens Settings", settings)
    moveMouse(to: CGPoint(x: display.midX, y: display.minY + 200))
} else {
    record("input-driven checks", false, "this runner does not let the process post mouse events; hover, click, drag and menu checks were not run")
}

record("the app is still running", app.isRunning, app.isRunning ? "" : "exited with status \(app.terminationStatus)", fatal: true)

// MARK: CPU and memory

func measure(label: String, extra: [String: String]) {
    if app.isRunning { kill(app.processIdentifier, SIGKILL); app.waitUntilExit() }
    guard let run = launch(extra: extra, log: "\(outFolder)/app-\(label).log") else { return }
    _ = waitFor(30) { drawer(pid: run.processIdentifier) != nil }
    moveMouse(to: CGPoint(x: display.midX, y: display.midY))
    pause(10)
    let (average, peak, memory) = sample(pid: run.processIdentifier, seconds: measureSeconds)
    let alive = run.isRunning
    let line = String(format: "%@: average %.1f%% of one core, peak %.1f%%, peak memory %.0f MB over %d s", label, average, peak, memory, measureSeconds)
    print(line)
    record("cpu \(label)", alive, line, fatal: !alive)
    appendLine(line, to: "\(outFolder)/cpu.txt")
    if run.isRunning { kill(run.processIdentifier, SIGKILL); run.waitUntilExit() }
}

measure(label: "default", extra: [:])
measure(label: "battery-saver", extra: ["EDGELINGS_ASSUME_BATTERY": "1"])

if let data = try? JSONEncoder().encode(steps) { try? data.write(to: URL(fileURLWithPath: "\(outFolder)/uitest.json")) }
let failed = steps.filter { !$0.ok && $0.fatal }
print("\(steps.count) checks, \(failed.count) failed")
exit(failed.isEmpty ? 0 : 1)
