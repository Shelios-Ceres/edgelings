// What Core's presence check needs from AppKit: for each app that owns windows,
// whether any is on screen. Owner process id and on-screen state only. Window
// titles are never read (kCGWindowName is not touched), which would also need
// the Screen Recording permission.
import AppKit
import CoreGraphics

enum WindowSnapshot {
    struct Entry {
        let pid: Int32
        /// No window of the app is on screen: all minimized, on another Space,
        /// or the app is hidden (Cmd-H).
        let offscreen: Bool
    }

    /// Ordinary windows (layer 0, visible, not a speck) grouped by owner.
    /// Menu-bar items, the Dock and overlays sit on other layers and are left out.
    static func capture() -> [Entry] {
        let options: CGWindowListOption = [.optionAll, .excludeDesktopElements]
        guard let list = CGWindowListCopyWindowInfo(options, kCGNullWindowID) as? [[String: Any]] else { return [] }
        var shown: [Int32: Bool] = [:]
        for window in list {
            guard let pid = (window[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value,
                  (window[kCGWindowLayer as String] as? NSNumber)?.intValue == 0,
                  ((window[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1) > 0.01 else { continue }
            if let bounds = window[kCGWindowBounds as String] as? [String: Any],
               let rect = CGRect(dictionaryRepresentation: bounds as CFDictionary),
               rect.width < 32 || rect.height < 32 { continue }
            let onScreen = (window[kCGWindowIsOnscreen as String] as? NSNumber)?.boolValue ?? false
            shown[pid] = (shown[pid] ?? false) || onScreen
        }
        return shown.map { pid, onScreen in
            let hidden = NSRunningApplication(processIdentifier: pid)?.isHidden ?? false
            return Entry(pid: pid, offscreen: !onScreen || hidden)
        }.sorted { $0.pid < $1.pid }
    }

    /// The bridge's "windows" request body.
    static func request() -> [String: Any] {
        ["windows": capture().map { ["pid": Int($0.pid), "offscreen": $0.offscreen] as [String: Any] }]
    }
}
