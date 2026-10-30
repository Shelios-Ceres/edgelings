// The Settings window's pages: Agents, Drawer, Usage & cost and General.
import SwiftUI
import AppKit

struct SettingsRoot: View {
    @ObservedObject var store: SettingsStore

    var body: some View {
        HStack(spacing: 0) {
            SettingsSidebar(store: store)
            Group {
                if RenderFlags.offscreen {
                    SettingsPageBody(store: store).padding(.horizontal, 24).padding(.top, 44).padding(.bottom, 24)
                        .frame(maxWidth: .infinity, alignment: .topLeading)
                } else {
                    ScrollViewReader { proxy in
                        ScrollView {
                            SettingsPageBody(store: store)
                                .padding(.horizontal, 24).padding(.top, 44).padding(.bottom, 24).frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .onChange(of: store.focus) { id in
                            guard let id else { return }
                            withAnimation { proxy.scrollTo(id, anchor: .top) }
                        }
                    }
                }
            }
            .background(store.palette.color(store.skin == .pixel ? "Desk" : "Layer"))
        }
        .frame(minWidth: 760, minHeight: 560)
        .tint(store.palette.color("Accent"))
    }
}

/// The page list. Pixel keeps the Windows pixel sidebar (kit icons, an
/// outlined selection with the accent bar); Classic is a macOS source list:
/// SF Symbols on the sidebar material, the selection tinted with the accent.
/// The window's title bar is transparent, so both run up under the traffic lights.
struct SettingsSidebar: View {
    @ObservedObject var store: SettingsStore

    var body: some View {
        let kit = Kit(store: store)
        let palette = store.palette
        VStack(alignment: .leading, spacing: 4) {
            ForEach(SettingsPage.allCases) { page in
                let selected = store.page == page
                Button(action: { store.page = page; store.refresh() }) {
                    HStack(spacing: 10) {
                        if kit.pixel {
                            PixelIcon(name: page.pixelIcon, ink: selected ? palette.color("Text") : palette.color("Text2"), cell: 2)
                        } else {
                            Image(systemName: page.symbol).frame(width: 18).foregroundColor(selected ? palette.color("Accent") : palette.color("Text2"))
                        }
                        Text(store.t(page.key)).font(kit.body()).foregroundColor(selected ? palette.color("Text") : palette.color("Text2"))
                        Spacer(minLength: 0)
                    }
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .background(selection(selected, palette: palette))
                    .overlay(alignment: .leading) {
                        if kit.pixel && selected { Rectangle().fill(palette.color("Accent")).frame(width: 4).padding(.vertical, 4) }
                    }
                }
                .buttonStyle(.plain)
            }
            Spacer()
        }
        .padding(.horizontal, 12).padding(.top, 44).padding(.bottom, 12)
        .frame(width: 190)
        .background(sidebarBackground(palette))
    }

    @ViewBuilder private func selection(_ selected: Bool, palette: Palette) -> some View {
        if !selected {
            Color.clear
        } else if store.skin == .pixel {
            PixelPlateBackground(palette: palette, outline: palette.color("Line"), fill: palette.color("Paper"))
        } else {
            RoundedRectangle(cornerRadius: 6).fill(palette.color("AccentTint"))
        }
    }

    @ViewBuilder private func sidebarBackground(_ palette: Palette) -> some View {
        if store.skin == .pixel || RenderFlags.offscreen {
            palette.color("Layer")
        } else {
            SidebarMaterial()
        }
    }
}

/// The system's sidebar material behind the Classic page list.
struct SidebarMaterial: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView()
        view.material = .sidebar
        view.blendingMode = .behindWindow
        view.state = .followsWindowActiveState
        return view
    }

    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}

struct SettingsPageBody: View {
    @ObservedObject var store: SettingsStore

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            switch store.page {
            case .agents: AgentsPage(store: store)
            case .drawer: DrawerPage(store: store)
            case .usage: UsagePage(store: store)
            case .general: GeneralPage(store: store)
            }
        }
    }
}

// MARK: Drawer

struct DrawerPage: View {
    @ObservedObject var store: SettingsStore

    var body: some View {
        PageTitle(store: store, key: "Drawer")
        PageIntro(store: store, text: store.t("The column of characters on the edge of your screen. Hover one for its status, click it for its settings, drag it to move the drawer, right-click it for options."))
        if let drawer = store.settings?.drawer {
            SectionLabel(store: store, text: store.t("Characters"))
            SettingRow(store: store, glyph: "E771", symbol: "paintpalette", title: store.t("Character style"), subtitle: store.t("Pixel bots drawn for each agent, or the classic shapes you can recolour")) {
                KitMenu(store: store, options: [store.t("Pixel"), store.t("Classic")], selected: drawer.theme == "pixel" ? 0 : 1) { index in
                    store.setDrawer(["theme": index == 0 ? "pixel" : "classic"])
                }
            }
            SettingRow(store: store, glyph: "E8BD", symbol: "text.bubble", title: store.t("Character lines"), subtitle: store.t("A character speaks up in a bubble when a session is done, asks you something, needs your approval or hits an error")) {
                KitToggle(store: store, isOn: drawer.speech) { store.setDrawer(["speech": $0]) }
            }
            SectionLabel(store: store, text: store.t("Placement"))
            SettingRow(store: store, glyph: "E8AB", symbol: "rectangle.righthalf.inset.filled", title: store.t("Edge"), subtitle: store.t("Which side of the screen the drawer docks to")) {
                KitMenu(store: store, options: [store.t("Right edge"), store.t("Left edge")], selected: drawer.side == "right" ? 0 : 1) { index in
                    store.setDrawer(["side": index == 0 ? "right" : "left"])
                }
            }
            let screens = NSScreen.screens
            let labels = [store.t("Automatic (primary display)")] + screens.enumerated().map { index, screen in
                store.t("Display {0}", index + 1) + "  \(Int(screen.frame.width))×\(Int(screen.frame.height))" + (index == 0 ? "  · " + store.t("primary") : "")
            }
            let chosen = drawer.display == "auto" ? 0 : (screens.firstIndex { DrawerController.displayId($0) == drawer.display }.map { $0 + 1 } ?? 0)
            SettingRow(store: store, glyph: "E7F4", symbol: "display", title: store.t("Display"), subtitle: store.t("A pinned display falls back to automatic while it is unplugged")) {
                KitMenu(store: store, options: labels, selected: chosen) { index in
                    store.setDrawer(["display": index == 0 ? "auto" : DrawerController.displayId(screens[index - 1])])
                }
            }
            SettingRow(store: store, glyph: "E7C2", symbol: "arrow.up.and.down", title: store.t("Position"), subtitle: store.t(drawer.locked
                ? "Characters stay put: dragging won't move the drawer, and a character appearing or leaving won't shift the ones above it"
                : "Drag any character up or down to move the drawer")) {
                HStack(spacing: 10) {
                    KitButton(store: store, title: store.t("Center on the edge"), enabled: !drawer.locked) { store.setDrawer(["offset": 0.5]) }
                    KitToggle(store: store, isOn: drawer.locked, onLabel: store.t("Locked"), offLabel: store.t("Unlocked")) { on in
                        if on, let pin = DrawerPlacementHelper.currentPinnedTop() { store.setDrawer(["locked": true, "pinnedTop": pin]) }
                        else if !on { store.setDrawer(["locked": false, "offset": DrawerPlacementHelper.currentOffset() ?? drawer.offset, "pinnedTop": NSNull()]) }
                        else { store.setDrawer(["locked": on]) }
                    }
                }
            }
            SectionLabel(store: store, text: store.t("Motion"))
            let rates = [0, 120, 60]
            SettingRow(store: store, glyph: "E916", symbol: "speedometer", title: store.t("Frame rate while an agent works"), subtitle: store.t("Calm characters always breathe at 30 fps")) {
                KitMenu(store: store, options: [store.t("Match the display"), store.t("{0} fps", 120), store.t("{0} fps", 60)],
                        selected: rates.firstIndex(of: drawer.fps) ?? 0) { index in store.setDrawer(["fps": rates[index]]) }
            }
            SettingRow(store: store, glyph: "E83F", symbol: "battery.100", title: store.t("Save battery"), subtitle: store.t("On battery, characters run at 60 fps at most")) {
                KitToggle(store: store, isOn: drawer.saveBattery) { store.setDrawer(["saveBattery": $0]) }
            }
            SettingRow(store: store, glyph: "E768", symbol: "sparkles", title: store.t("Animation"),
                       subtitle: store.t("With Reduce motion on in System Settings, characters slow down and stop following the pointer")) {
                KitButton(store: store, title: store.t("Open System Settings")) {
                    if let url = URL(string: "x-apple.systempreferences:com.apple.Accessibility-Settings.extension?Display") { NSWorkspace.shared.open(url) }
                }
            }
        }
    }
}

/// The drawer's place on screen, for the lock switch (Swift owns the window).
enum DrawerPlacementHelper {
    static var drawer: DrawerController?

    static func currentPinnedTop() -> Double? {
        guard let drawer, let screen = drawer.screen else { return nil }
        return Double(screen.visibleFrame.maxY - drawer.panel.frame.maxY)
    }

    static func currentOffset() -> Double? {
        guard let drawer, let screen = drawer.screen else { return nil }
        let travel = screen.visibleFrame.height - drawer.panel.frame.height
        guard travel > 0 else { return 0.5 }
        return Double(min(max((screen.visibleFrame.maxY - drawer.panel.frame.maxY) / travel, 0), 1))
    }
}

// MARK: General

struct GeneralPage: View {
    @ObservedObject var store: SettingsStore

    var body: some View {
        PageTitle(store: store, key: "General")
        SectionLabel(store: store, text: store.t("Startup"))
        SettingRow(store: store, glyph: "E7E8", symbol: "power", title: store.t("Launch at login"),
                   subtitle: LaunchAtLogin.needsApproval ? store.t("Login Items need your approval in System Settings") : store.t("Start Edgelings when you sign in to your Mac")) {
            KitToggle(store: store, isOn: store.launchAtLogin) { on in
                LaunchAtLogin.setEnabled(on)
                store.launchAtLogin = LaunchAtLogin.isEnabled
            }
        }
        SectionLabel(store: store, text: store.t("Language"))
        if let language = store.settings?.language {
            SettingRow(store: store, glyph: "F2B7", symbol: "globe", title: store.t("App language"), subtitle: store.skin == .pixel ? store.t("The pixel style shows Settings in English") : nil) {
                KitMenu(store: store, options: [store.t("Same as system"), "English", "简体中文"], selected: ["auto": 0, "en": 1, "zh": 2][language] ?? 0) { index in
                    store.send("set", ["language": ["auto", "en", "zh"][index]]) { store.refresh() }
                }
            }
        }
        SectionLabel(store: store, text: store.t("About"))
        VStack(alignment: .leading, spacing: 8) {
            Text("Edgelings").font(Kit(store: store).strong(15)).foregroundColor(store.palette.color("Text"))
            Text("Mac test build").font(Kit(store: store).body(12)).foregroundColor(store.palette.color("Text2"))
            Text(store.t("Thanks")).font(Kit(store: store).strong(12)).foregroundColor(store.palette.color("Text")).padding(.top, 6)
            Text("Agent Island").font(Kit(store: store).body(12)).foregroundColor(store.palette.color("Text"))
            Text(store.t("The original app, by {0} · MIT License", "Tristan Tang")).font(Kit(store: store).body(12)).foregroundColor(store.palette.color("Text2"))
            if RenderFlags.offscreen {
                Text("github.com/tristan666666/agent-island").font(Kit(store: store).body(12)).foregroundColor(store.palette.color("Accent"))
            } else {
                Link("github.com/tristan666666/agent-island", destination: URL(string: "https://github.com/tristan666666/agent-island")!).font(Kit(store: store).body(12))
            }
            Text("Ark Pixel").font(Kit(store: store).body(12)).foregroundColor(store.palette.color("Text")).padding(.top, 4)
            Text(store.t("The pixel font, by {0} · SIL Open Font License 1.1", "TakWolf")).font(Kit(store: store).body(12)).foregroundColor(store.palette.color("Text2"))
            HStack(spacing: 8) {
                KitButton(store: store, title: store.t("Copy diagnostic")) { DiagnosticReport.copy() }
                KitButton(store: store, title: store.t("Open folder")) {
                    let folder = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first?.appendingPathComponent("Edgelings")
                    if let folder { try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true); NSWorkspace.shared.open(folder) }
                }
                KitButton(store: store, title: store.t("Quit")) { NSApp.terminate(nil) }
            }.padding(.top, 8)
            Text(store.t("Ids, counts and states only: no titles or message text")).font(Kit(store: store).body(11)).foregroundColor(store.palette.color("Text3"))
        }
        .padding(16).frame(maxWidth: .infinity, alignment: .leading)
        .modifier(CardSurface(store: store))
    }
}
