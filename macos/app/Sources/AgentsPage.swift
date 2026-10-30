// Settings > Agents, as AgentSection.cs lays it out: a header with the
// agent's mark or face, its name and plan, its status, arrows and a Shown
// switch; then rows, each with a leading icon, a title over a description, and
// its control on the right: the Look tiles, one row per quota window, Tokens,
// a note, the account, Sign in and the optional permission prompts.
import SwiftUI
import AppKit

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

/// A row: a leading icon, a title over a description, a control on the right.
struct IconRow<Control: View>: View {
    @ObservedObject var store: SettingsStore
    let glyph: String
    let symbol: String
    let title: String
    var subtitle: String?
    var divider = true
    @ViewBuilder var control: Control

    var body: some View {
        let kit = Kit(store: store)
        let palette = store.palette
        VStack(spacing: 0) {
            if divider {
                if kit.pixel { DottedRule(color: palette.color("LineSoft"), vertical: false) }
                else { Rectangle().fill(palette.color("LineSoft")).frame(height: 1).padding(.leading, 44) }
            }
            HStack(alignment: .center, spacing: 12) {
                RowIcon(store: store, glyph: glyph, symbol: symbol, bare: true)
                VStack(alignment: .leading, spacing: 1) {
                    SkinText(pixel: store.skin == .pixel, text: title, pixelSize: 13, size: 13, weight: .regular, color: palette.color("Text"))
                    if let subtitle, !subtitle.isEmpty {
                        SkinText(pixel: store.skin == .pixel, text: subtitle, pixelSize: 12, size: 12, weight: .regular, color: palette.color("Text2")).fixedSize(horizontal: false, vertical: true)
                    }
                }
                Spacer(minLength: 8)
                control
            }
            .padding(.vertical, 10)
        }
    }
}

/// The leading icon of a row: a Pixel kit icon, or an SF Symbol in a round puck.
struct RowIcon: View {
    @ObservedObject var store: SettingsStore
    let glyph: String
    let symbol: String
    /// Inside an agent's card Classic's icon is a bare symbol in a 32-pt slot, so titles line up under the name.
    var bare = false

    var body: some View {
        if store.skin == .pixel {
            Group {
                if let name = PixelArt.glyphs[glyph] { PixelIcon(name: name, ink: store.palette.color("Text2"), cell: 2) }
                else { Color.clear }
            }
            .frame(width: 20, height: 20)
        } else if bare {
            Image(systemName: symbol).font(.system(size: 16)).foregroundColor(store.palette.color("Text2")).frame(width: 32, height: 32)
        } else {
            ZStack {
                Circle().fill(store.palette.color("Hover")).frame(width: 32, height: 32)
                Image(systemName: symbol).font(.system(size: 14)).foregroundColor(store.palette.color("Text2"))
            }
        }
    }
}

struct AgentCard: View {
    @ObservedObject var store: SettingsStore
    let agent: AgentModel
    let index: Int?
    let count: Int
    let move: (Int) -> Void

    private var theme: String { store.skin == .pixel ? "pixel" : "classic" }
    private var worn: String { store.skin == .pixel ? agent.look.pixel : agent.look.classic }

    var body: some View {
        let kit = Kit(store: store)
        let palette = store.palette
        let ink = palette.agentInk(id: agent.id, swatch: agent.swatch, defaultLook: agent.defaultLook)
        VStack(alignment: .leading, spacing: 0) {
            header(kit: kit, palette: palette)
            if agent.detected {
                lookRow(kit: kit, palette: palette)
                ForEach(Array(agent.meters.enumerated()), id: \.offset) { _, meter in
                    meterRow(meter, ink: ink, kit: kit, palette: palette)
                }
                if let tokens = agent.tokens {
                    IconRow(store: store, glyph: "E8EF", symbol: "number", title: store.t("Tokens"), subtitle: store.quota.tokenModeLabel) {
                        SkinText(pixel: store.skin == .pixel, text: "\(tokens.today)  ·  \(tokens.month)", pixelSize: 12, size: 12, weight: .regular, color: palette.color("Text2"))
                    }
                }
                if let note = agent.note, !note.isEmpty {
                    IconRow(store: store, glyph: "E946", symbol: "info.circle", title: note) { EmptyView() }
                }
            }
            if let warning = agent.warning, !warning.isEmpty {
                IconRow(store: store, glyph: "E946", symbol: "info.circle", title: warning) { EmptyView() }
            }
            if agent.id == "codex" || agent.account != nil {
                IconRow(store: store, glyph: "E77B", symbol: "person.crop.circle", title: store.t("Account")) {
                    HStack(spacing: 10) {
                        SkinText(pixel: store.skin == .pixel, text: agent.account ?? store.t("Not saved yet"), pixelSize: 12, size: 12, weight: .regular, color: palette.color("Text2"))
                        if agent.id == "codex" { codexMenu }
                    }
                }
            }
            if agent.id == "claude" || agent.id == "codex" {
                IconRow(store: store, glyph: "E8D7", symbol: "key", title: store.t("Sign in"), subtitle: agent.signInHint) {
                    KitButton(store: store, title: store.t(agent.signIn == "waiting" ? "Waiting for login…" : agent.signIn == "available" ? "Sign in" : "Sign in again"),
                              prominent: false, enabled: agent.signIn != "waiting") {
                        store.send("agent", ["action": "signIn", "id": agent.id])
                    }
                }
            }
            if agent.id == "claude" || agent.id == "grok" { hookRow(kit: kit) }
        }
        .padding(.horizontal, 16).padding(.vertical, 6)
        .modifier(CardSurface(store: store))
        .id(agent.id)
        .onAppear { store.loadLooks(for: agent.id) }
    }

    // MARK: Header

    private func header(kit: Kit, palette: Palette) -> some View {
        HStack(spacing: 12) {
            AgentMark(store: store, agent: agent)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 8) {
                    SkinText(pixel: store.skin == .pixel, text: agent.name, pixelSize: 13, size: 14, weight: .regular, color: agent.shown ? palette.color("Text") : palette.color("Text3"))
                    if let plan = agent.plan, !plan.isEmpty {
                        SkinText(pixel: kit.pixel, text: plan, pixelSize: 13, size: 10, weight: .semibold,
                                 color: kit.pixel ? palette.agentInk(id: agent.id, swatch: agent.swatch, defaultLook: agent.defaultLook) : palette.color("Text"))
                            .padding(.horizontal, 6).padding(.vertical, 1)
                            .background(kit.pixel ? AnyView(Rectangle().fill(palette.tint(agent.swatch))) : AnyView(Capsule().fill(palette.tint(agent.swatch))))
                    }
                }
                SkinText(pixel: store.skin == .pixel, text: agent.status, pixelSize: 12, size: 12, weight: .regular, color: palette.color("Text2"))
            }
            Spacer(minLength: 8)
            if let index {
                arrow("chevron.up", enabled: index > 0) { move(-1) }
                arrow("chevron.down", enabled: index < count - 1) { move(1) }
            }
            SkinText(pixel: store.skin == .pixel, text: store.t(agent.shown ? "Shown" : "Hidden"), pixelSize: 12, size: 12, weight: .regular, color: palette.color("Text2"))
            KitToggle(store: store, isOn: agent.shown, showsLabel: false) { on in store.setDrawer(["shown": ["id": agent.id, "on": on]]) }
        }
        .padding(.vertical, 10)
    }

    private func arrow(_ symbol: String, enabled: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Group {
                if store.skin == .pixel {
                    PixelIcon(name: symbol == "chevron.up" ? "up" : "down", ink: enabled ? store.palette.color("Text") : store.palette.color("Disabled"), cell: 2)
                } else {
                    Image(systemName: symbol).font(.system(size: 10, weight: .bold)).foregroundColor(store.palette.color("Text"))
                }
            }
            .frame(width: 28, height: 28)
            .background(store.skin == .pixel
                ? AnyView(PixelPlateBackground(palette: store.palette, outline: enabled ? store.palette.color("LineControl") : store.palette.color("Disabled"),
                                               fill: enabled ? store.palette.color("Field") : store.palette.color("DisabledFill")))
                : AnyView(RoundedRectangle(cornerRadius: 6).fill(store.palette.color("Field"))))
        }
        .buttonStyle(.plain).disabled(!enabled).opacity(enabled || store.skin == .pixel ? 1 : 0.35)
    }

    // MARK: Rows

    private func lookRow(kit: Kit, palette: Palette) -> some View {
        let choices = store.looks["\(agent.id)-\(theme)"] ?? []
        let name = choices.first { $0.id == worn }?.name ?? ""
        let description = kit.pixel ? store.t("{0}. What {1} wears in the drawer, on its card and in menus", name, agent.name) : name
        return VStack(spacing: 0) {
            if kit.pixel { DottedRule(color: palette.color("LineSoft"), vertical: false) } else { Rectangle().fill(palette.color("LineSoft")).frame(height: 1) }
            ViewThatFits(in: .horizontal) {
                HStack(alignment: .center, spacing: 12) {
                    lookLabel(description)
                    Spacer(minLength: 8)
                    if !choices.isEmpty { tiles(choices) }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                VStack(alignment: .leading, spacing: 8) {
                    lookLabel(description)
                    if !choices.isEmpty { tiles(choices) }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .padding(.vertical, 10)
        }
    }

    private func lookLabel(_ description: String) -> some View {
        HStack(spacing: 12) {
            RowIcon(store: store, glyph: "E771", symbol: "paintpalette", bare: true)
            VStack(alignment: .leading, spacing: 1) {
                SkinText(pixel: store.skin == .pixel, text: store.t("Look"), pixelSize: 13, size: 13, color: store.palette.color("Text"))
                SkinText(pixel: store.skin == .pixel, text: description, pixelSize: 13, size: 12, color: store.palette.color("Text2"))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func tiles(_ choices: [LookChoice]) -> some View {
        HStack(alignment: .top, spacing: 8) {
            ForEach(choices) { choice in
                LookTile(store: store, agent: agent, choice: choice, worn: choice.id == worn) {
                    store.setDrawer(["look": ["id": agent.id, "theme": theme, "look": choice.id]])
                }
            }
        }
    }

    private func meterRow(_ meter: MeterModel, ink: Color, kit: Kit, palette: Palette) -> some View {
        let warn = (meter.used ?? 0) >= 0.9
        return IconRow(store: store, glyph: "E9D2", symbol: "chart.bar", title: meter.title ?? meter.label, subtitle: meter.resets) {
            HStack(spacing: 12) {
                Group {
                    if kit.pixel { PixelBar(share: meter.shown, color: warn ? palette.color("Critical") : ink, blocks: 30) }
                    else { ClassicBar(share: meter.shown, color: warn ? palette.color("Critical") : ink, track: palette.tint(agent.swatch)).frame(width: 180) }
                }
                SkinText(pixel: kit.pixel, text: meter.value, pixelSize: 13, size: 12, weight: warn && !kit.pixel ? .semibold : .regular,
                         color: warn ? palette.color("Critical") : palette.color("Text2"))
                    .frame(minWidth: kit.pixel ? 64 : 40, alignment: .trailing)
            }
        }
    }

    private var codexMenu: some View {
        let codex = store.settings?.codex
        return Group {
            if RenderFlags.offscreen {
                KitMenu(store: store, options: [store.t("Switch")], selected: 0) { _ in }
            } else {
                Menu {
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
                    HStack(spacing: 6) {
                        SkinText(pixel: store.skin == .pixel, text: store.t("Switch"), pixelSize: 13, size: 13, color: store.palette.color("Text"))
                        if store.skin == .pixel { PixelIcon(name: "down", ink: store.palette.color("Text2"), cell: 2).frame(width: 20, height: 20) }
                    }
                }
                .menuStyle(.borderlessButton).menuIndicator(store.skin == .pixel ? .hidden : .visible).fixedSize()
                .padding(.horizontal, 10).padding(.vertical, 5).modifier(MenuPlate(store: store))
            }
        }
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
    private func hookRow(kit: Kit) -> some View {
        let on = agent.id == "claude" ? store.settings?.hooks.claude ?? false : store.settings?.hooks.grok ?? false
        let conflict = agent.id == "grok" && (store.settings?.hooks.grokConflict ?? false)
        let description = agent.id == "claude"
            ? store.t("Shows when Claude Code waits for you to approve a tool. Adds a small hook to Claude Code's settings.json that records only the session and the prompt type; sessions started afterwards pick it up.")
            : conflict
                ? store.t("A file named agentisland-notify.json is already in ~/.grok/hooks and isn't Edgelings', so this stays off.")
                : store.t("Shows when Grok waits for you to approve a tool. Adds one hook file, agentisland-notify.json, to ~/.grok/hooks that records only the session and the prompt type; sessions started afterwards pick it up.")
        return IconRow(store: store, glyph: "E8D7", symbol: "lock.shield", title: store.t("Permission prompts"), subtitle: description) {
            KitToggle(store: store, isOn: on) { wanted in
                store.send("agent", ["action": "hook", "id": agent.id, "on": wanted])
            }
            .disabled(conflict)
        }
    }
}

/// The header's mark: Pixel's face for the look worn, Classic's brand mark on the agent's tint.
struct AgentMark: View {
    @ObservedObject var store: SettingsStore
    let agent: AgentModel

    var body: some View {
        if store.skin == .pixel {
            AgentFace(id: agent.id, look: agent.look.pixel, swatch: agent.swatch, skin: .pixel, size: 30)
        } else {
            ZStack {
                Circle().fill(store.palette.tint(agent.swatch)).frame(width: 32, height: 32)
                if let image = BrandMarks.image(agent.id) {
                    Image(nsImage: image).renderingMode(.template).resizable().interpolation(.high).scaledToFit().frame(width: 18, height: 18)
                        .foregroundColor(store.palette.agentInk(id: agent.id, swatch: agent.swatch, defaultLook: agent.defaultLook))
                } else {
                    FaceChip(swatch: agent.swatch, size: 18)
                }
            }
        }
    }
}

/// The agents' brand marks (the PNGs the Windows app ships), from Resources/Marks.
enum BrandMarks {
    private static var cache: [String: NSImage] = [:]

    static func image(_ id: String) -> NSImage? {
        if let known = cache[id] { return known }
        let file = id == "codex" ? "openai" : id
        let base = ProcessInfo.processInfo.environment["EDGELINGS_MARKS"] ?? Bundle.main.resourceURL?.appendingPathComponent("Marks").path ?? ""
        guard let image = NSImage(contentsOfFile: base + "/mark-\(file).png") else { return nil }
        cache[id] = image
        return image
    }
}

/// One look to pick: Pixel's 44-pt tile with the look's face, Classic's 80-pt
/// tile with the look's desk companion over its name.
struct LookTile: View {
    @ObservedObject var store: SettingsStore
    let agent: AgentModel
    let choice: LookChoice
    let worn: Bool
    let pick: () -> Void

    var body: some View {
        let palette = store.palette
        Button(action: pick) {
            if store.skin == .pixel {
                AgentFace(id: agent.id, look: choice.id, swatch: choice.swatch, skin: .pixel, size: 30)
                    .frame(width: 44, height: 44)
                    .background(PixelPlateBackground(palette: palette, outline: worn ? palette.color("Accent") : palette.color("Line"), fill: palette.color("Paper")))
                    .overlay(alignment: .bottomTrailing) {
                        if worn {
                            PixelIcon(name: "check", ink: palette.color("OnAccent"), cell: 1)
                                .frame(width: 10, height: 10)
                                .padding(2)
                                .background(Rectangle().fill(palette.color("Accent")))
                                .padding(2)
                        }
                    }
            } else {
                VStack(spacing: 2) {
                    if let image = BotPreview.image(theme: "classic", kind: agent.id, look: choice.id) {
                        Image(decorative: image, scale: 2).interpolation(.high).frame(width: 44, height: 44)
                    } else {
                        FaceChip(swatch: choice.swatch, size: 30).frame(width: 44, height: 44)
                    }
                    Text(choice.name)
                        .font(.system(size: 11, weight: worn ? .semibold : .regular))
                        .foregroundColor(worn ? palette.color("AccentText") : palette.color("Text"))
                        .multilineTextAlignment(.center).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                        .padding(.horizontal, 6)
                }
                .padding(.top, 6).padding(.bottom, 6)
                .frame(width: 80, alignment: .top)
                .frame(minHeight: 76, alignment: .top)
                .background(RoundedRectangle(cornerRadius: 10).fill(worn ? palette.color("AccentTint") : palette.color("Paper")))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(worn ? palette.color("Accent") : palette.hairline, lineWidth: worn ? 2 : 1))
            }
        }
        .buttonStyle(.plain)
    }
}

/// A character's resting frame in a look, drawn by the engine, cached.
enum BotPreview {
    private static var engines: [String: CharacterEngine] = [:]
    private static var cache: [String: CGImage] = [:]

    static func image(theme: String, kind: String, look: String) -> CGImage? {
        let key = "\(theme)/\(kind)/\(look)"
        if let known = cache[key] { return known }
        if engines[theme] == nil { engines[theme] = try? CharacterEngine(theme: theme, directory: AppController.enginesDirectory()) }
        guard let engine = engines[theme] else { return nil }
        let index = engine.addBot(kind: kind, tempo: 1, seed: 7, look: look)
        guard index >= 0 else { return nil }
        var dts = [Double?](repeating: nil, count: index + 1)
        dts[index] = 0.1
        var svg: String?
        for _ in 0..<6 { svg = engine.tick(dts, size: 44)[index]?.svg ?? svg }
        guard let svg else { return nil }
        let side = 88
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.translateBy(x: 0, y: CGFloat(side))
        ctx.scaleBy(x: 2, y: -2)
        try? SvgCanvas().render(svg: svg, into: ctx, antialias: theme != "pixel")
        let image = ctx.makeImage()
        if let image { cache[key] = image }
        return image
    }
}
