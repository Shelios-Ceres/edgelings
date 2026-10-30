// Edgelings for macOS: an accessory app (no Dock icon) with the edge drawer, a
// menu-bar item, and a Settings window.
import AppKit
import SwiftUI

@main
struct EdgelingsMain {
    @MainActor
    static func main() {
        let app = NSApplication.shared
        let arguments = CommandLine.arguments
        if arguments.contains("--self-test") { SelfTest.run() }
        if arguments.contains("--diagnose") { DiagnosticReport.printAndExit() }
        if let at = arguments.firstIndex(of: "--render"), at + 1 < arguments.count {
            RenderMode.run(into: arguments[at + 1])
            exit(0)
        }
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
        DrawerPlacementHelper.drawer = drawer
        drawer.openSettings = { id in SettingsController.shared.show(agent: id) }
        drawer.showMenu = { [weak self] id, point in self?.showDrawerMenu(for: id) }
        controller.onFrame = { [weak self] frame in
            guard let self else { return }
            self.drawer.apply(frame)
            self.drawer.refreshCard()
            if self.statusItem == nil { self.installStatusItem() }
            if self.iconPixel != frame.isPixel {
                self.iconPixel = frame.isPixel
                self.statusItem?.button?.image = MenuBarIcon.image(pixel: frame.isPixel)
            }
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
        item.button?.image = MenuBarIcon.image(pixel: AppController.shared.frame?.isPixel ?? true)
        iconPixel = AppController.shared.frame?.isPixel ?? true
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
        trayMenu = menu
        item.button?.target = self
        item.button?.action = #selector(trayClicked)
        item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        statusItem = item
        refreshTooltip()
    }

    private var iconPixel = true
    private var trayMenu: NSMenu?
    private var trayCardTimer: Timer?

    /// A left click shows every agent in one card for a few seconds; a right
    /// click (or control-click) opens the menu.
    @objc private func trayClicked() {
        guard let event = NSApp.currentEvent, let item = statusItem else { return }
        if event.type == .rightMouseUp || event.modifierFlags.contains(.control) {
            item.menu = trayMenu
            item.button?.performClick(nil)
            item.menu = nil
            return
        }
        drawer.setShown(true)
        drawer.openCard(nil)
        trayCardTimer?.invalidate()
        trayCardTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: false) { [weak self] _ in
            self?.drawer.closeAllCard()
        }
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
        let theme = frame.isPixel ? "pixel" : "classic"
        // The looks of the character that was clicked, for the Look submenu.
        AppController.shared.request("looks", ["id": id, "theme": theme]) { [weak self] reply in
            let looks = LooksModel.from(reply)?.looks ?? []
            self?.popUpDrawerMenu(for: id, frame: frame, looks: looks)
        }
    }

    private func popUpDrawerMenu(for id: String, frame: FrameModel, looks: [LookChoice]) {
        guard let bot = frame.bots.first(where: { $0.id == id }) else { return }
        let pixel = frame.isPixel
        let dark = DrawerController.isDark
        // Under Pixel the items carry the kit's icons (and faces), in the system font.
        func icon(_ name: String) -> NSImage? { pixel ? MenuImages.pixelIcon(name, dark: dark) : nil }
        func item(_ title: String, _ action: Selector, _ iconName: String? = nil) -> NSMenuItem {
            let entry = NSMenuItem(title: title, action: action, keyEquivalent: "")
            entry.target = self
            if let iconName { entry.image = icon(iconName) }
            return entry
        }

        let menu = NSMenu()
        let settings = item("Settings…", #selector(openAgentSettings(_:)), "general")
        settings.representedObject = id
        menu.addItem(settings)
        menu.addItem(.separator())

        if looks.count > 1 {
            let submenu = NSMenu()
            let worn = bot.look
            for look in looks {
                let entry = NSMenuItem(title: look.name, action: #selector(pickLook(_:)), keyEquivalent: "")
                entry.target = self
                entry.representedObject = ["id": id, "theme": theme(pixel), "look": look.id]
                entry.state = look.id == worn ? .on : .off
                entry.image = MenuImages.face(agent: id, look: look.id, swatch: look.swatch, pixel: pixel)
                submenu.addItem(entry)
            }
            let lookItem = NSMenuItem(title: "Look", action: nil, keyEquivalent: "")
            lookItem.image = icon("palette")
            lookItem.submenu = submenu
            menu.addItem(lookItem)
        }

        let shown = NSMenu()
        for other in frame.bots where other.id != "cursor" {
            let entry = NSMenuItem(title: other.name, action: #selector(toggleAgent(_:)), keyEquivalent: "")
            entry.target = self
            entry.representedObject = other.id
            entry.state = other.visible ? .on : .off
            entry.image = MenuImages.face(agent: other.id, look: other.look, swatch: other.swatch, pixel: pixel)
            shown.addItem(entry)
        }
        let shownItem = NSMenuItem(title: "Shown agents", action: nil, keyEquivalent: "")
        shownItem.image = icon("agents")
        shownItem.submenu = shown
        menu.addItem(shownItem)

        menu.addItem(item(frame.locked ? "Unlock position" : "Lock position", #selector(toggleLock), "lock"))
        menu.addItem(item(frame.side == "right" ? "Move to left edge" : "Move to right edge", #selector(switchSide), "edge"))
        menu.addItem(item(frame.isPixel ? "Classic characters" : "Pixel characters", #selector(switchStyle), "shapes"))
        let speech = item("Character lines", #selector(toggleSpeech), "bubble")
        speech.state = frame.speechBubbles ? .on : .off
        menu.addItem(speech)
        menu.addItem(.separator())
        menu.addItem(item("Hide drawer", #selector(toggleDrawer), "eye"))
        menu.addItem(item("Quit Edgelings", #selector(quit), "power"))
        menu.popUp(positioning: nil, at: NSEvent.mouseLocation, in: nil)
    }

    private func theme(_ pixel: Bool) -> String { pixel ? "pixel" : "classic" }

    @objc private func pickLook(_ sender: NSMenuItem) {
        guard let choice = sender.representedObject as? [String: String], let id = choice["id"] else { return }
        AppController.shared.request("setDrawer", ["look": choice])
        _ = id
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
