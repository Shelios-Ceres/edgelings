// Edgelings for macOS: an accessory app (no Dock icon) with the edge drawer, a
// menu-bar item, and a Settings window.
import AppKit
import SwiftUI

@main
struct EdgelingsMain {
    static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var drawer: DrawerController!
    private var statusItem: NSStatusItem?
    private var events: PlatformEvents?
    private var quotaTimer: Timer?
    private var toggleItem: NSMenuItem?

    func applicationDidFinishLaunching(_ notification: Notification) {
        PixelFace.register()
        let controller = AppController.shared
        drawer = DrawerController()
        drawer.openSettings = { id in SettingsController.shared.show(agent: id) }
        drawer.showMenu = { [weak self] id, point in self?.showDrawerMenu(for: id) }
        controller.onFrame = { [weak self] frame in
            guard let self else { return }
            self.drawer.apply(frame)
            self.drawer.refreshCard()
            if self.statusItem == nil { self.installStatusItem() }
        }
        controller.start()
        if let bridge = controller.bridge {
            events = PlatformEvents { [weak self] kind in
                DispatchQueue.global(qos: .utility).async { bridge.request("systemEvent", ["kind": kind]) }
                DispatchQueue.main.async {
                    switch kind {
                    case "sleep", "lock": self?.drawer.setAway(true)
                    case "wake", "unlock": self?.drawer.setAway(false)
                    default: break
                    }
                }
            }
            events?.start()
        }
        let workspace = NSWorkspace.shared.notificationCenter
        workspace.addObserver(forName: NSWorkspace.screensDidSleepNotification, object: nil, queue: .main) { [weak self] _ in self?.drawer.setAway(true) }
        workspace.addObserver(forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) { [weak self] _ in self?.drawer.setAway(false) }
        installStatusItem()
        quotaTimer = Timer.scheduledTimer(withTimeInterval: 30, repeats: true) { [weak self] _ in self?.refreshTooltip() }
        // Launching the app again brings the drawer back.
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        drawer.setShown(true)
        return false
    }

    func applicationWillTerminate(_ notification: Notification) {
        events?.stop()
    }

    // MARK: Menu bar

    private func installStatusItem() {
        guard statusItem == nil else { return }
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = MenuBarIcon.image()
        item.button?.toolTip = "Edgelings"
        let menu = NSMenu()
        menu.delegate = nil
        let toggle = NSMenuItem(title: "Hide drawer", action: #selector(toggleDrawer), keyEquivalent: "")
        toggle.target = self
        menu.addItem(toggle)
        toggleItem = toggle
        menu.addItem(.separator())
        let settings = NSMenuItem(title: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
        settings.target = self
        menu.addItem(settings)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit Edgelings", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
        item.menu = menu
        statusItem = item
        refreshTooltip()
    }

    @objc private func toggleDrawer() {
        let show = !drawer.isShown
        drawer.setShown(show)
        toggleItem?.title = show ? "Hide drawer" : "Show drawer"
    }

    @objc private func openSettings() { SettingsController.shared.show(agent: nil) }

    @objc private func quit() { NSApp.terminate(nil) }

    /// The quota tooltip: each shown agent's windows on one line.
    private func refreshTooltip() {
        AppController.shared.cards(for: nil) { [weak self] model in
            guard let model else { return }
            let lines = model.cards.map { card -> String in
                let meters = card.meters.map { "\($0.label) \($0.value)" }.joined(separator: " · ")
                return meters.isEmpty ? card.name : "\(card.name): \(meters)"
            }
            self?.statusItem?.button?.toolTip = lines.isEmpty ? "Edgelings" : lines.joined(separator: "\n")
        }
    }

    // MARK: The drawer's right-click menu

    private func showDrawerMenu(for id: String) {
        guard let frame = AppController.shared.frame else { return }
        let menu = NSMenu()
        let settings = NSMenuItem(title: "Settings…", action: #selector(openAgentSettings(_:)), keyEquivalent: "")
        settings.target = self
        settings.representedObject = id
        menu.addItem(settings)
        menu.addItem(.separator())

        let shown = NSMenu()
        for bot in frame.bots where bot.id != "cursor" {
            let item = NSMenuItem(title: bot.name, action: #selector(toggleAgent(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = bot.id
            item.state = bot.visible ? .on : .off
            shown.addItem(item)
        }
        let shownItem = NSMenuItem(title: "Shown agents", action: nil, keyEquivalent: "")
        shownItem.submenu = shown
        menu.addItem(shownItem)

        let lock = NSMenuItem(title: frame.locked ? "Unlock position" : "Lock position", action: #selector(toggleLock), keyEquivalent: "")
        lock.target = self
        menu.addItem(lock)
        let side = NSMenuItem(title: frame.side == "right" ? "Move to left edge" : "Move to right edge", action: #selector(switchSide), keyEquivalent: "")
        side.target = self
        menu.addItem(side)
        let style = NSMenuItem(title: frame.isPixel ? "Classic characters" : "Pixel characters", action: #selector(switchStyle), keyEquivalent: "")
        style.target = self
        menu.addItem(style)
        let speech = NSMenuItem(title: "Character lines", action: #selector(toggleSpeech), keyEquivalent: "")
        speech.target = self
        speech.state = frame.speechBubbles ? .on : .off
        menu.addItem(speech)
        menu.addItem(.separator())
        let hide = NSMenuItem(title: "Hide drawer", action: #selector(toggleDrawer), keyEquivalent: "")
        hide.target = self
        menu.addItem(hide)
        let quit = NSMenuItem(title: "Quit Edgelings", action: #selector(quit), keyEquivalent: "")
        quit.target = self
        menu.addItem(quit)
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
    }

    @objc private func openAgentSettings(_ sender: NSMenuItem) { SettingsController.shared.show(agent: sender.representedObject as? String) }

    @objc private func toggleAgent(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        AppController.shared.request("setDrawer", ["shown": ["id": id, "on": sender.state != .on]])
    }

    @objc private func toggleLock() {
        guard let frame = AppController.shared.frame, let screen = drawer.screen else { return }
        let panel = drawer.panel.frame
        let work = screen.visibleFrame
        if frame.locked {
            let travel = work.height - panel.height
            let offset = travel > 0 ? Double(min(max((work.maxY - panel.maxY) / travel, 0), 1)) : 0.5
            AppController.shared.request("setDrawer", ["locked": false, "offset": offset, "pinnedTop": NSNull()])
        } else {
            AppController.shared.request("setDrawer", ["locked": true, "pinnedTop": Double(work.maxY - panel.maxY)])
        }
    }

    @objc private func switchSide() {
        guard let frame = AppController.shared.frame else { return }
        AppController.shared.request("setDrawer", ["side": frame.side == "right" ? "left" : "right"])
    }

    @objc private func switchStyle() {
        guard let frame = AppController.shared.frame else { return }
        AppController.shared.request("setDrawer", ["theme": frame.isPixel ? "classic" : "pixel"])
    }

    @objc private func toggleSpeech() {
        guard let frame = AppController.shared.frame else { return }
        AppController.shared.request("setDrawer", ["speech": !frame.speechBubbles])
    }
}

/// The menu-bar icon: a small robot head, drawn as a template image so the
/// system tints it for light and dark bars.
enum MenuBarIcon {
    static func image() -> NSImage {
        let size = NSSize(width: 18, height: 18)
        let image = NSImage(size: size, flipped: false) { rect in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: NSRect(x: 2, y: 3, width: 14, height: 11), xRadius: 3, yRadius: 3).fill()
            NSBezierPath(rect: NSRect(x: 8, y: 14, width: 2, height: 3)).fill()
            NSColor.clear.setFill()
            let eyes = NSBezierPath()
            eyes.append(NSBezierPath(rect: NSRect(x: 5, y: 7, width: 2, height: 3)))
            eyes.append(NSBezierPath(rect: NSRect(x: 11, y: 7, width: 2, height: 3)))
            NSGraphicsContext.current?.compositingOperation = .clear
            eyes.fill()
            return true
        }
        image.isTemplate = true
        return image
    }
}
