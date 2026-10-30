// Settings: stage 3 fills this in. Until then the window says so.
import AppKit
import SwiftUI

final class SettingsController {
    static let shared = SettingsController()
    private var window: NSWindow?

    func show(agent: String?) {
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 400), styleMask: [.titled, .closable],
                                  backing: .buffered, defer: false)
            window.title = "Edgelings Settings"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: Text("Settings").padding(40))
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}
