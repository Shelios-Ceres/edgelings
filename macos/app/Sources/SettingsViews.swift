// The Settings window's pages: Agents, Drawer, Usage & cost and General.
import SwiftUI
import AppKit

struct SettingsRoot: View {
    @ObservedObject var store: SettingsStore

    var body: some View {
        let palette = store.palette
        let kit = Kit(store: store)
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(SettingsPage.allCases) { page in
                    Button(action: { store.page = page; store.refresh() }) {
                        HStack(spacing: 8) {
                            Image(systemName: page.symbol).frame(width: 18)
                            Text(store.t(page.key)).font(kit.body())
                            Spacer(minLength: 0)
                        }
                        .foregroundColor(store.page == page ? palette.color("Text") : palette.color("Text2"))
                        .padding(.horizontal, 10).padding(.vertical, 7)
                        .background(RoundedRectangle(cornerRadius: kit.pixel ? 0 : 6).fill(store.page == page ? palette.color("Hover") : Color.clear))
                    }
                    .buttonStyle(.plain)
                }
                Spacer()
            }
            .padding(12).frame(width: 190)
            .background(palette.color("Layer"))
            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    switch store.page {
                    case .agents: AgentsPage(store: store)
                    case .drawer: DrawerPage(store: store)
                    case .usage: UsagePage(store: store)
                    case .general: GeneralPage(store: store)
                    }
                }
                .padding(24).frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(palette.color("Desk"))
        }
        .frame(minWidth: 760, minHeight: 560)
    }
}

// MARK: Agents

struct AgentsPage: View {
    @ObservedObject var store: SettingsStore

    var body: some View {
        PageTitle(store: store, key: "Agents")
        PageIntro(store: store, text: store.t("Quota, tokens and live activity for every coding agent on this computer, in the drawer's order. Drag a card by its grip or use its arrows to move it; switch one off to hide it everywhere."))
        if let agents = store.settings?.agents {
            let shown = agents.filter { $0.shown }.map(\.id)
            ForEach(agents) { agent in
                AgentCard(store: store, agent: agent, index: shown.firstIndex(of: agent.id), count: shown.count, move: { step in move(agent.id, step, shown) })
            }
        }
    }

    private func move(_ id: String, _ step: Int, _ shown: [String]) {
        guard let from = shown.firstIndex(of: id) else { return }
        let to = from + step
        guard shown.indices.contains(to) else { return }
        var order = shown
        order.swapAt(from, to)
        store.setDrawer(["order": order])
    }
}

struct AgentCard: View {
    @ObservedObject var store: SettingsStore
    let agent: AgentModel
    let index: Int?
    let count: Int
    let move: (Int) -> Void

    var body: some View {
        let kit = Kit(store: store)
        let palette = store.palette
        let identity = palette.readable(agent.accent)
        let theme = store.skin == .pixel ? "pixel" : "classic"
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                if kit.pixel { Rectangle().fill(Color(hex: agent.accent)).frame(width: 8, height: 8) }
                else { Circle().fill(Color(hex: agent.accent)).frame(width: 18, height: 18) }
                Text(agent.name).font(kit.strong(15)).foregroundColor(palette.color("Text"))
                if let plan = agent.plan, !plan.isEmpty {
                    Text(plan).font(kit.pixel ? PixelFace.font(10) : .system(size: 10, weight: .semibold)).foregroundColor(identity)
                        .padding(.horizontal, 6).padding(.vertical, 1).background(palette.tint(agent.accent))
                }
                Spacer()
                if let index {
                    Button(action: { move(-1) }) { Image(systemName: "chevron.up") }.buttonStyle(.plain).disabled(index == 0).opacity(index == 0 ? 0.3 : 1)
                    Button(action: { move(1) }) { Image(systemName: "chevron.down") }.buttonStyle(.plain).disabled(index == count - 1).opacity(index == count - 1 ? 0.3 : 1)
                }
                KitToggle(store: store, isOn: agent.shown) { on in store.setDrawer(["shown": ["id": agent.id, "on": on]]) }
            }
            Text(agent.status).font(kit.body(12)).foregroundColor(palette.color("Text2"))
            if let account = agent.account {
                Text(account).font(kit.body(12)).foregroundColor(palette.color("Text3"))
            }
            if let warning = agent.warning {
                Text(warning).font(kit.body(12)).foregroundColor(palette.color("Critical")).fixedSize(horizontal: false, vertical: true)
            }
            if agent.detected {
                ForEach(Array(agent.meters.enumerated()), id: \.offset) { _, meter in
                    HStack(spacing: 10) {
                        Text(meter.label).font(kit.body(12)).foregroundColor(palette.color("Text2")).frame(width: 60, alignment: .leading)
                        let warn = (meter.used ?? 0) >= 0.9
                        Group {
                            if kit.pixel { PixelBar(share: meter.shown, color: warn ? palette.color("Critical") : identity, blocks: 30) }
                            else { ClassicBar(share: meter.shown, color: warn ? palette.color("Critical") : identity, track: identity.opacity(0.2)) }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        Text(meter.value).font(kit.body(12)).foregroundColor(warn ? palette.color("Critical") : palette.color("Text2"))
                            .frame(width: 80, alignment: .trailing)
                        if let resets = meter.resets {
                            Text(resets).font(kit.body(11)).foregroundColor(palette.color("Text3")).frame(width: 120, alignment: .leading)
                        }
                    }
                }
                if let tokens = agent.tokens {
                    HStack(spacing: 10) {
                        Text(tokens.label).font(kit.body(12)).foregroundColor(palette.color("Text2")).frame(width: 60, alignment: .leading)
                        Text(tokens.today).font(kit.body(12)).foregroundColor(palette.color("Text"))
                        Text("·").foregroundColor(palette.color("Text3"))
                        Text(tokens.month).font(kit.body(12)).foregroundColor(palette.color("Text"))
                    }
                }
                if let note = agent.note {
                    Text(note).font(kit.body(11)).foregroundColor(palette.color("Text3")).fixedSize(horizontal: false, vertical: true)
                }
            }
            HStack(spacing: 8) {
                KitButton(store: store, title: store.t("Refresh")) { store.send("agent", ["action": "refresh", "id": agent.id]) }
                if agent.signIn != "none" {
                    KitButton(store: store, title: agent.signIn == "waiting" ? store.t("Waiting for sign-in") : store.t("Sign in"),
                              prominent: true, enabled: agent.signIn == "available") {
                        store.send("agent", ["action": "signIn", "id": agent.id])
                    }
                }
                lookMenu(theme)
                if agent.id == "codex" { codexMenu }
                Spacer()
            }
            if agent.id == "claude" || agent.id == "grok" { hookRow }
        }
        .padding(16)
        .modifier(CardSurface(store: store))
        .onAppear { store.loadLooks(for: agent.id) }
    }

    @ViewBuilder private func lookMenu(_ theme: String) -> some View {
        let choices = store.looks["\(agent.id)-\(theme)"] ?? []
        if choices.count > 1 {
            let current = theme == "pixel" ? agent.look.pixel : agent.look.classic
            KitMenu(store: store, options: choices.map(\.name), selected: choices.firstIndex { $0.id == current } ?? 0) { index in
                store.setDrawer(["look": ["id": agent.id, "theme": theme, "look": choices[index].id]])
            }
        }
    }

    private var codexMenu: some View {
        let codex = store.settings?.codex
        return Menu {
            if codex?.accounts.isEmpty ?? true { Text(store.t("No saved accounts yet")) }
            ForEach(codex?.accounts ?? []) { account in
                Button((account.label == codex?.active ? "✓ " : "") + account.label + (account.email.map { "  (\($0))" } ?? "")) {
                    store.send("agent", ["action": "codexSwitch", "id": "codex", "label": account.label])
                }
            }
            Divider()
            Button(store.t("Save current account…")) { saveAccount() }
            Button((codex?.autoSwitch == true ? "✓ " : "") + store.t("Auto-switch when exhausted")) {
                store.send("agent", ["action": "codexAuto", "id": "codex", "on": !(codex?.autoSwitch ?? false)])
            }
            if !(codex?.accounts.isEmpty ?? true) {
                Menu(store.t("Remove saved account")) {
                    ForEach(codex?.accounts ?? []) { account in
                        Button(account.label) { store.send("agent", ["action": "codexForget", "id": "codex", "label": account.label]) }
                    }
                }
            }
        } label: {
            Text(store.t("Switch")).font(Kit(store: store).body())
        }
        .menuStyle(.borderlessButton).fixedSize()
    }

    private func saveAccount() {
        let alert = NSAlert()
        alert.messageText = store.t("Save current account…")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 240, height: 24))
        field.placeholderString = "work / personal"
        alert.accessoryView = field
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        if alert.runModal() == .alertFirstButtonReturn {
            store.send("agent", ["action": "codexSave", "id": "codex", "label": field.stringValue])
        }
    }

    /// The optional permission-prompt hook: off until the user flips it.
    private var hookRow: some View {
        let on = agent.id == "claude" ? store.settings?.hooks.claude ?? false : store.settings?.hooks.grok ?? false
        let conflict = agent.id == "grok" && (store.settings?.hooks.grokConflict ?? false)
        return VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(store.t("Permission hook")).font(Kit(store: store).body()).foregroundColor(store.palette.color("Text"))
                Spacer()
                KitToggle(store: store, isOn: on) { wanted in
                    store.send("agent", ["action": "hook", "id": agent.id, "on": wanted])
                }
                .disabled(conflict)
            }
            Text(store.t("Lets the character say when a permission prompt is open. Adds one entry to the agent's settings (a backup is kept); nothing is installed until you switch it on."))
                .font(Kit(store: store).body(11)).foregroundColor(store.palette.color("Text3")).fixedSize(horizontal: false, vertical: true)
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
            SettingRow(store: store, title: store.t("Character style"), subtitle: store.t("Pixel bots drawn for each agent, or the classic shapes you can recolour")) {
                KitMenu(store: store, options: [store.t("Pixel"), store.t("Classic")], selected: drawer.theme == "pixel" ? 0 : 1) { index in
                    store.setDrawer(["theme": index == 0 ? "pixel" : "classic"])
                }
            }
            SettingRow(store: store, title: store.t("Character lines"), subtitle: store.t("A character speaks up in a bubble when a session is done, asks you something, needs your approval or hits an error")) {
                KitToggle(store: store, isOn: drawer.speech) { store.setDrawer(["speech": $0]) }
            }
            SectionLabel(store: store, text: store.t("Placement"))
            SettingRow(store: store, title: store.t("Edge"), subtitle: store.t("Which side of the screen the drawer docks to")) {
                KitMenu(store: store, options: [store.t("Right edge"), store.t("Left edge")], selected: drawer.side == "right" ? 0 : 1) { index in
                    store.setDrawer(["side": index == 0 ? "right" : "left"])
                }
            }
            SettingRow(store: store, title: store.t("Position"), subtitle: store.t(drawer.locked
                ? "Characters stay put: dragging won't move the drawer, and a character appearing or leaving won't shift the ones above it"
                : "Drag any character up or down to move the drawer")) {
                HStack(spacing: 10) {
                    KitButton(store: store, title: store.t("Center on the edge"), enabled: !drawer.locked) { store.setDrawer(["offset": 0.5]) }
                    KitToggle(store: store, isOn: drawer.locked) { on in
                        if on, let pin = DrawerPlacementHelper.currentPinnedTop() { store.setDrawer(["locked": true, "pinnedTop": pin]) }
                        else if !on { store.setDrawer(["locked": false, "offset": DrawerPlacementHelper.currentOffset() ?? drawer.offset, "pinnedTop": NSNull()]) }
                        else { store.setDrawer(["locked": on]) }
                    }
                }
            }
            SectionLabel(store: store, text: store.t("Motion"))
            let rates = [0, 120, 60]
            SettingRow(store: store, title: store.t("Frame rate while an agent works"), subtitle: store.t("Calm characters always breathe at 30 fps")) {
                KitMenu(store: store, options: [store.t("Match the display"), store.t("{0} fps", 120), store.t("{0} fps", 60)],
                        selected: rates.firstIndex(of: drawer.fps) ?? 0) { index in store.setDrawer(["fps": rates[index]]) }
            }
            SettingRow(store: store, title: store.t("Save battery"), subtitle: store.t("On battery, characters run at 60 fps at most")) {
                KitToggle(store: store, isOn: drawer.saveBattery) { store.setDrawer(["saveBattery": $0]) }
            }
            SettingRow(store: store, title: store.t("Animation"),
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
        SettingRow(store: store, title: store.t("Launch at login"),
                   subtitle: LaunchAtLogin.needsApproval ? store.t("Login Items need your approval in System Settings") : store.t("Start Edgelings when you sign in to your Mac")) {
            KitToggle(store: store, isOn: store.launchAtLogin) { on in
                LaunchAtLogin.setEnabled(on)
                store.launchAtLogin = LaunchAtLogin.isEnabled
            }
        }
        SectionLabel(store: store, text: store.t("Language"))
        if let language = store.settings?.language {
            SettingRow(store: store, title: store.t("App language"), subtitle: store.skin == .pixel ? store.t("The pixel style shows Settings in English") : nil) {
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
            Link("github.com/tristan666666/agent-island", destination: URL(string: "https://github.com/tristan666666/agent-island")!).font(Kit(store: store).body(12))
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
