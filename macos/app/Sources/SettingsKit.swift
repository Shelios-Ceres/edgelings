// The controls both skins' Settings pages are made of: a card, a switch, a
// menu, a button, a page title. Pixel draws them as stepped plates in the
// pixel face; Classic as rounded paper with the system's own look.
import SwiftUI

/// ImageRenderer cannot draw AppKit-backed controls (Menu); offscreen renders show their label instead.
enum RenderFlags {
    static var offscreen = false
}

struct Kit {
    let store: SettingsStore
    var palette: Palette { store.palette }
    var pixel: Bool { store.skin == .pixel }

    func body(_ size: CGFloat = 13) -> Font { pixel ? PixelFace.font(size) : .system(size: size) }
    func strong(_ size: CGFloat = 13) -> Font { pixel ? PixelFace.font(size) : .system(size: size, weight: .semibold) }
}

struct PageTitle: View {
    @ObservedObject var store: SettingsStore
    let key: String
    var body: some View {
        let kit = Kit(store: store)
        SkinText(pixel: store.skin == .pixel, text: store.t(key), pixelSize: 26, size: 26, weight: .semibold, color: kit.palette.color("Text"))
    }
}

struct PageIntro: View {
    @ObservedObject var store: SettingsStore
    let text: String
    var body: some View {
        SkinText(pixel: store.skin == .pixel, text: text, pixelSize: 13, size: 13, weight: .regular, color: store.palette.color("Text2")).fixedSize(horizontal: false, vertical: true)
    }
}

struct SectionLabel: View {
    @ObservedObject var store: SettingsStore
    let text: String
    var body: some View {
        if store.skin == .pixel {
            HStack(spacing: 8) {
                Rectangle().fill(store.palette.color("Accent")).frame(width: 6, height: 6)
                SkinText(pixel: store.skin == .pixel, text: text, pixelSize: 13, size: 13, weight: .regular, color: store.palette.color("Accent"))
            }
            .padding(.top, 12)
        } else {
            Text(text).font(.system(size: 14, weight: .semibold)).foregroundColor(store.palette.color("Text")).padding(.top, 12)
        }
    }
}

/// A setting: a title, what it does, and its control on the right.
struct SettingRow<Control: View>: View {
    @ObservedObject var store: SettingsStore
    var glyph = ""
    var symbol = ""
    let title: String
    var subtitle: String?
    @ViewBuilder var control: Control

    var body: some View {
        let kit = Kit(store: store)
        HStack(alignment: .center, spacing: 12) {
            if !glyph.isEmpty { RowIcon(store: store, glyph: glyph, symbol: symbol) }
            VStack(alignment: .leading, spacing: 2) {
                SkinText(pixel: store.skin == .pixel, text: title, pixelSize: 13, size: 13, weight: .regular, color: kit.palette.color("Text"))
                if let subtitle {
                    SkinText(pixel: store.skin == .pixel, text: subtitle, pixelSize: 12, size: 12, weight: .regular, color: kit.palette.color("Text2")).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 8)
            control
        }
        .padding(.horizontal, 16).padding(.vertical, 12)
        .modifier(CardSurface(store: store))
    }
}

struct CardSurface: ViewModifier {
    @ObservedObject var store: SettingsStore
    func body(content: Content) -> some View {
        if store.skin == .pixel {
            content.background(PixelPlateBackground(palette: store.palette, outline: store.palette.color("Line"), fill: store.palette.color("Paper")))
                .padding(2)
        } else {
            content.background(
                RoundedRectangle(cornerRadius: 14).fill(store.palette.color("Paper"))
                    .overlay(RoundedRectangle(cornerRadius: 14).stroke(store.palette.hairline, lineWidth: 1)))
        }
    }
}

struct KitToggle: View {
    @ObservedObject var store: SettingsStore
    let isOn: Bool
    var showsLabel = true
    var onLabel: String?
    var offLabel: String?
    let change: (Bool) -> Void

    var body: some View {
        if store.skin == .pixel {
            Button(action: { change(!isOn) }) {
                HStack(spacing: 8) {
                    if showsLabel {
                        SkinText(pixel: store.skin == .pixel, text: isOn ? (onLabel ?? store.t("On")) : (offLabel ?? store.t("Off")), pixelSize: 13, size: 13, weight: .regular, color: store.palette.color("Text2"))
                    }
                    ZStack(alignment: isOn ? .trailing : .leading) {
                        Rectangle().fill(isOn ? store.palette.color("Accent") : store.palette.color("DisabledFill"))
                            .frame(width: 36, height: 16)
                            .overlay(Rectangle().stroke(store.palette.color("LineControl"), lineWidth: 2))
                        Rectangle().fill(isOn ? store.palette.color("KnobOn") : store.palette.color("Text2")).frame(width: 10, height: 10).padding(.horizontal, 3)
                    }
                }
            }
            .buttonStyle(.plain)
        } else if RenderFlags.offscreen {
            HStack(spacing: 8) {
                if let label = isOn ? onLabel : offLabel { Text(label).font(.system(size: 12)).foregroundColor(store.palette.color("Text2")) }
                Capsule().fill(isOn ? store.palette.color("Accent") : store.palette.color("Field"))
                    .frame(width: 38, height: 22)
                    .overlay(Circle().fill(Color.white).padding(2), alignment: isOn ? .trailing : .leading)
            }
        } else {
            HStack(spacing: 8) {
                if let label = isOn ? onLabel : offLabel {
                    Text(label).font(.system(size: 12)).foregroundColor(store.palette.color("Text2"))
                }
                Toggle("", isOn: Binding(get: { isOn }, set: { change($0) })).toggleStyle(.switch).labelsHidden().tint(store.palette.color("Accent"))
            }
        }
    }
}

struct KitMenu: View {
    @ObservedObject var store: SettingsStore
    let options: [String]
    let selected: Int
    let change: (Int) -> Void

    private var label: some View {
        let text = options.indices.contains(selected) ? options[selected] : (options.first ?? "")
        return HStack(spacing: 6) {
            SkinText(pixel: store.skin == .pixel, text: text, pixelSize: 13, size: 13, weight: .regular, color: store.palette.color("Text"))
            // Pixel draws its own chevron; Classic keeps the system's.
            if store.skin == .pixel {
                PixelIcon(name: "down", ink: store.palette.color("Text2"), cell: 2).frame(width: 20, height: 20)
            } else if RenderFlags.offscreen {
                Image(systemName: "chevron.up.chevron.down").font(.system(size: 9)).foregroundColor(store.palette.color("Text2"))
            }
        }
    }

    var body: some View {
        if RenderFlags.offscreen {
            label.padding(.horizontal, 10).padding(.vertical, 5).modifier(MenuPlate(store: store))
        } else {
            Menu {
                ForEach(Array(options.enumerated()), id: \.offset) { index, option in
                    Button(option) { change(index) }
                }
            } label: {
                label
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(store.skin == .pixel ? .hidden : .visible)
            .fixedSize()
            .padding(.horizontal, 10).padding(.vertical, 5)
            .modifier(MenuPlate(store: store))
        }
    }
}

struct MenuPlate: ViewModifier {
    @ObservedObject var store: SettingsStore
    func body(content: Content) -> some View {
        content.background(store.skin == .pixel
            ? AnyView(PixelPlateBackground(palette: store.palette, outline: store.palette.color("LineControl"), fill: store.palette.color("Field")))
            : AnyView(RoundedRectangle(cornerRadius: 6).fill(store.palette.color("Field"))))
    }
}

struct KitButton: View {
    @ObservedObject var store: SettingsStore
    let title: String
    var prominent = false
    var enabled = true
    let action: () -> Void

    var body: some View {
        let palette = store.palette
        Button(action: action) {
            SkinText(pixel: store.skin == .pixel, text: title, pixelSize: 13, size: 13, color: prominent ? palette.color("OnAccent") : palette.color("Text"))
                .padding(.horizontal, 12).padding(.vertical, 5)
                .background(
                    store.skin == .pixel
                        ? AnyView(PixelPlateBackground(palette: palette, outline: palette.color(prominent ? "AccentDeep" : "LineControl"),
                                                        fill: palette.color(prominent ? "Accent" : "Field")))
                        : AnyView(RoundedRectangle(cornerRadius: 6).fill(palette.color(prominent ? "Accent" : "Field"))))
        }
        .buttonStyle(.plain)
        .opacity(enabled ? 1 : 0.45)
        .disabled(!enabled)
    }
}

/// A line of bars for a small chart: blocks in the pixel skin, rounded in Classic.
struct BarChart: View {
    let values: [Double]
    let color: Color
    let track: Color
    let pixel: Bool
    var height: CGFloat = 70

    var body: some View {
        let peak = max(values.max() ?? 0, 0.0001)
        HStack(alignment: .bottom, spacing: pixel ? 2 : 3) {
            ForEach(Array(values.enumerated()), id: \.offset) { _, value in
                let share = CGFloat(value / peak)
                ZStack(alignment: .bottom) {
                    Rectangle().fill(track).frame(height: height)
                    if pixel {
                        Rectangle().fill(color).frame(height: value > 0 ? max(2, (height * share / 2).rounded() * 2) : 0)
                    } else {
                        RoundedRectangle(cornerRadius: 2).fill(color).frame(height: value > 0 ? max(2, height * share) : 0)
                    }
                }
                .frame(maxWidth: .infinity)
            }
        }
        .frame(height: height)
    }
}

/// Pixel's chart: each hour or day a stack of blocks, over a dotted baseline.
struct BlockChart: View {
    let values: [Double]
    let color: Color
    let baseline: Color
    var rows = 14

    var body: some View {
        let peak = max(values.max() ?? 0, 0.0001)
        VStack(spacing: 2) {
            HStack(alignment: .bottom, spacing: 2) {
                ForEach(Array(values.enumerated()), id: \.offset) { _, value in
                    let blocks = value > 0 ? max(1, Int((value / peak * Double(rows)).rounded())) : 0
                    VStack(spacing: 2) {
                        ForEach(0..<rows, id: \.self) { row in
                            Rectangle().fill(row >= rows - blocks ? color : Color.clear).frame(height: 3)
                        }
                    }
                    .frame(maxWidth: .infinity)
                }
            }
            DottedRule(color: baseline, vertical: false, dash: [6, 4])
        }
    }
}
