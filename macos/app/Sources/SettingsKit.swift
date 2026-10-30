// The controls both skins' Settings pages are made of: a card, a switch, a
// menu, a button, a page title. Pixel draws them as stepped plates in the
// pixel face; Classic as rounded paper with the system's own look.
import SwiftUI

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
        Text(store.t(key)).font(kit.pixel ? PixelFace.font(26) : .system(size: 26, weight: .semibold)).foregroundColor(kit.palette.color("Text"))
    }
}

struct PageIntro: View {
    @ObservedObject var store: SettingsStore
    let text: String
    var body: some View {
        Text(text).font(Kit(store: store).body()).foregroundColor(store.palette.color("Text2")).fixedSize(horizontal: false, vertical: true)
    }
}

struct SectionLabel: View {
    @ObservedObject var store: SettingsStore
    let text: String
    var body: some View {
        Text(text).font(Kit(store: store).strong(14)).foregroundColor(store.palette.color("Text")).padding(.top, 12)
    }
}

/// A setting: a title, what it does, and its control on the right.
struct SettingRow<Control: View>: View {
    @ObservedObject var store: SettingsStore
    let title: String
    var subtitle: String?
    @ViewBuilder var control: Control

    var body: some View {
        let kit = Kit(store: store)
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(kit.body()).foregroundColor(kit.palette.color("Text"))
                if let subtitle {
                    Text(subtitle).font(kit.body(12)).foregroundColor(kit.palette.color("Text2")).fixedSize(horizontal: false, vertical: true)
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
                RoundedRectangle(cornerRadius: 8).fill(store.palette.color("Paper"))
                    .overlay(RoundedRectangle(cornerRadius: 8).stroke(store.palette.hairline, lineWidth: 1)))
        }
    }
}

struct KitToggle: View {
    @ObservedObject var store: SettingsStore
    let isOn: Bool
    let change: (Bool) -> Void

    var body: some View {
        if store.skin == .pixel {
            Button(action: { change(!isOn) }) {
                HStack(spacing: 6) {
                    ZStack(alignment: isOn ? .trailing : .leading) {
                        Rectangle().fill(isOn ? store.palette.color("Accent") : store.palette.color("DisabledFill"))
                            .frame(width: 36, height: 16)
                            .overlay(Rectangle().stroke(store.palette.color("LineControl"), lineWidth: 2))
                        Rectangle().fill(isOn ? store.palette.color("KnobOn") : store.palette.color("Text2")).frame(width: 10, height: 10).padding(.horizontal, 3)
                    }
                    Text(store.t(isOn ? "On" : "Off")).font(PixelFace.font(12)).foregroundColor(store.palette.color("Text2"))
                }
            }
            .buttonStyle(.plain)
        } else {
            Toggle("", isOn: Binding(get: { isOn }, set: { change($0) })).toggleStyle(.switch).labelsHidden()
        }
    }
}

struct KitMenu: View {
    @ObservedObject var store: SettingsStore
    let options: [String]
    let selected: Int
    let change: (Int) -> Void

    var body: some View {
        let label = options.indices.contains(selected) ? options[selected] : (options.first ?? "")
        Menu {
            ForEach(Array(options.enumerated()), id: \.offset) { index, option in
                Button(option) { change(index) }
            }
        } label: {
            Text(label).font(Kit(store: store).body()).foregroundColor(store.palette.color("Text"))
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .padding(.horizontal, 10).padding(.vertical, 5)
        .background(store.skin == .pixel
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
            Text(title).font(Kit(store: store).body())
                .foregroundColor(prominent ? palette.color("OnAccent") : palette.color("Text"))
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
