// Whether another app is full screen on a display's current Space. A panel at the
// status-bar level that can join every Space shows over a full-screen app whether
// or not it asks to (the real-screen check saw it), so with "In full screen" off the
// drawer has to step aside itself: it looks for a window of another app that covers
// the whole display (owner, layer and size only, never a title).
//
// On a notched display (a MacBook Pro's built-in screen) a full-screen window sits
// below the camera housing: its bounds are the display minus `safeAreaInsets.top`, so
// that form counts too, but only while no menu bar is showing, or a maximised window
// under the menu bar would match it.
import AppKit
import CoreGraphics

enum FullScreenWatch {
    private static func near(_ a: CGFloat, _ b: CGFloat) -> Bool { abs(a - b) <= 1.5 }

    /// A window the size of the whole display, or (with a notch and no menu bar) the display below the notch.
    static func covers(_ window: CGRect, display: CGRect, notch: CGFloat = 0, menuBarShowing: Bool = false) -> Bool {
        guard near(window.minX, display.minX), near(window.width, display.width) else { return false }
        if near(window.minY, display.minY), near(window.height, display.height) { return true }
        return notch > 0 && !menuBarShowing && near(window.minY, display.minY + notch) && near(window.maxY, display.maxY)
    }

    /// Another process has an ordinary window covering `screen` on the current Space.
    static func isCovered(screen: NSScreen?) -> Bool {
        guard let screen, let number = screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber else { return false }
        let display = CGDisplayBounds(CGDirectDisplayID(number.uint32Value))
        let notch = screen.safeAreaInsets.top
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return false }
        let me = ProcessInfo.processInfo.processIdentifier
        func bounds(_ entry: [String: Any]) -> CGRect? {
            guard let dictionary = entry[kCGWindowBounds as String] as? [String: Any] else { return nil }
            return CGRect(dictionaryRepresentation: dictionary as CFDictionary)
        }
        // The menu bar is a window of its own (layer 24) along the top of the display while it shows.
        let menuBarShowing = list.contains { entry in
            guard (entry[kCGWindowLayer as String] as? NSNumber)?.intValue == 24, let rect = bounds(entry) else { return false }
            return rect.width >= display.width - 2 && abs(rect.minY - display.minY) <= 1.5
        }
        for entry in list {
            guard (entry[kCGWindowLayer as String] as? NSNumber)?.intValue == 0,
                  let owner = (entry[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value, owner != me,
                  ((entry[kCGWindowAlpha as String] as? NSNumber)?.doubleValue ?? 1) > 0,
                  let rect = bounds(entry) else { continue }
            if covers(rect, display: display, notch: notch, menuBarShowing: menuBarShowing) { return true }
        }
        return false
    }

    /// Samples for the self-test: a window and a display (and its notch and menu bar), and whether the window covers it.
    static let samples: [(window: CGRect, display: CGRect, notch: CGFloat, menuBar: Bool, covers: Bool, name: String)] = [
        (CGRect(x: 0, y: 0, width: 1512, height: 982), CGRect(x: 0, y: 0, width: 1512, height: 982), 0, false, true, "a window the size of the display"),
        (CGRect(x: 0, y: 38, width: 1512, height: 944), CGRect(x: 0, y: 0, width: 1512, height: 982), 0, true, false, "a maximised window under the menu bar"),
        (CGRect(x: 1512, y: 0, width: 1920, height: 1080), CGRect(x: 0, y: 0, width: 1512, height: 982), 0, false, false, "a full-screen window on another display"),
        (CGRect(x: -1920, y: 0, width: 1920, height: 1080), CGRect(x: -1920, y: 0, width: 1920, height: 1080), 0, false, true, "a display left of the main one"),
        (CGRect(x: 0, y: 32, width: 1512, height: 950), CGRect(x: 0, y: 0, width: 1512, height: 982), 32, false, true, "a full-screen window below a notch"),
        (CGRect(x: 0, y: 32, width: 1512, height: 950), CGRect(x: 0, y: 0, width: 1512, height: 982), 32, true, false, "the same bounds with the menu bar showing is a zoomed window"),
        (CGRect(x: 0, y: 37, width: 1512, height: 945), CGRect(x: 0, y: 0, width: 1512, height: 982), 32, false, false, "a window under a 37-point menu bar is not below the notch"),
        (CGRect(x: 0, y: 32, width: 1512, height: 950), CGRect(x: 0, y: 0, width: 1512, height: 982), 0, false, false, "below-notch bounds on a display without a notch"),
    ]
}
