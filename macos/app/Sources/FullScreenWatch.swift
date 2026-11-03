// Whether another app is full screen on a display's current Space. A panel at the
// status-bar level that can join every Space shows over a full-screen app whether
// or not it asks to (the real-screen check saw it), so with "In full screen" off the
// drawer has to step aside itself: it looks for a window of another app that covers
// the whole display (owner, layer and size only, never a title).
import AppKit
import CoreGraphics

enum FullScreenWatch {
    /// A window the size of the whole display (a full-screen app's), within a point.
    static func covers(_ window: CGRect, display: CGRect) -> Bool {
        abs(window.minX - display.minX) <= 1 && abs(window.minY - display.minY) <= 1
            && abs(window.width - display.width) <= 1 && abs(window.height - display.height) <= 1
    }

    /// Another process has an ordinary window covering `screen` on the current Space.
    static func isCovered(screen: NSScreen?) -> Bool {
        guard let screen, let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return false }
        let display = CGDisplayBounds(CGDirectDisplayID(number.uint32Value))
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return false }
        let me = ProcessInfo.processInfo.processIdentifier
        for entry in list {
            guard (entry[kCGWindowLayer as String] as? NSNumber)?.intValue == 0,
                  let owner = (entry[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value, owner != me,
                  ((entry[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1) > 0,
                  let bounds = entry[kCGWindowBounds as String] as? [String: Any],
                  let rect = CGRect(dictionaryRepresentation: bounds as CFDictionary) else { continue }
            if covers(rect, display: display) { return true }
        }
        return false
    }

    /// Samples for the self-test: a window and a display, and whether the window covers it.
    static let samples: [(window: CGRect, display: CGRect, covers: Bool, name: String)] = [
        (CGRect(x: 0, y: 0, width: 1512, height: 982), CGRect(x: 0, y: 0, width: 1512, height: 982), true, "a window the size of the display"),
        (CGRect(x: 0, y: 38, width: 1512, height: 944), CGRect(x: 0, y: 0, width: 1512, height: 982), false, "a maximised window under the menu bar"),
        (CGRect(x: 1512, y: 0, width: 1920, height: 1080), CGRect(x: 0, y: 0, width: 1512, height: 982), false, "a full-screen window on another display"),
        (CGRect(x: -1920, y: 0, width: 1920, height: 1080), CGRect(x: -1920, y: 0, width: 1920, height: 1080), true, "a display left of the main one"),
    ]
}
