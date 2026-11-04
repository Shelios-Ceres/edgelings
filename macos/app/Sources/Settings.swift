// The Settings window: Agents, Drawer, Usage & cost and General, in the skin
// of the drawer's character style. It is one ordinary titled window that
// hides when closed and comes back on its last page.
import AppKit
import SwiftUI

final class SettingsController: NSObject, NSWindowDelegate {
    static let shared = SettingsController()
    let store = SettingsStore()
    private var window: NSWindow?

    func show(agent: String?) {
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 880, height: 640),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView], backing: .buffered, defer: false)
            window.title = "Edgelings"
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: SettingsRoot(store: store))
            window.delegate = self
            window.setFrameAutosaveName("EdgelingsSettings")
            window.center()
            self.window = window
        }
        if agent != nil { store.page = .agents }
        store.focus = agent
        store.start()
        // An accessory app has no Dock icon to bring its window forward.
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        store.stop()
        // The window and its SwiftUI tree go with it (the next show builds them again): a closed Settings window
        // kept its pages and their caches, tens of megabytes of the app's memory.
        let closing = window
        window = nil
        DispatchQueue.main.async { closing?.contentView = nil }
    }
}
