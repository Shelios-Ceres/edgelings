// The two kits' design tokens (PixelTokens.cs, ClassicTokens.cs), the pixel
// face, and the pieces both skins' panels are made of.
import SwiftUI
import AppKit
import CoreText

enum Skin {
    case pixel, classic
}

struct Palette {
    let skin: Skin
    let dark: Bool

    private static let pixel: [String: (UInt32, UInt32)] = [
        "Desk": (0xEFE9DC, 0x141519), "Layer": (0xF7F3EA, 0x1A1B21), "Paper": (0xFFFDF7, 0x1F2027),
        "Field": (0xFAF6EC, 0x262731), "FieldHover": (0xF1EBDD, 0x2E2F3A), "FieldPressed": (0xE8E1D0, 0x22232B),
        "Hover": (0xECE5D6, 0x24252E), "Pressed": (0xE3DBC9, 0x2A2B35), "Line": (0x2B2B33, 0x5A5B66),
        "LineControl": (0x2B2B33, 0x8E8F9C), "LineSoft": (0xCFC6B3, 0x34353F), "Shadow": (0xD6CDB9, 0x0B0B0F),
        "Text": (0x2B2B33, 0xECE9E1), "Text2": (0x5A5865, 0xB8B5C1), "Text3": (0x6B6976, 0x9D9AA8),
        "Disabled": (0xA6A2AC, 0x5E5D68), "DisabledFill": (0xEEE9DF, 0x22232A), "Accent": (0x6B4EE6, 0xA58CFF),
        "AccentHover": (0x5B3FD8, 0xB6A1FF), "AccentPressed": (0x4B32BE, 0x9177F2), "AccentDeep": (0x3B2794, 0x6E55D6),
        "OnAccent": (0xFFFFFF, 0x1B1238), "AccentTint": (0xE9E3FF, 0x2E2852), "Caution": (0x8A5300, 0xF2B544),
        "Critical": (0xC42B1C, 0xFF99A4), "Smoke": (0x2B2B33, 0x000000), "KnobOn": (0xFFFDF7, 0x1B1238),
    ]

    private static let classic: [String: (UInt32, UInt32)] = [
        "Desk": (0xEDEFF2, 0x1A1B1F), "Layer": (0xF6F7F9, 0x202126), "Paper": (0xFFFFFF, 0x2A2B31),
        "Field": (0xF1F2F5, 0x34353C), "FieldHover": (0xE9EBEF, 0x3C3D45), "FieldPressed": (0xE1E4E9, 0x2F3036),
        "Hover": (0xF1F2F5, 0x313238), "Pressed": (0xE8EAEE, 0x2C2D33), "LineSoft": (0xE4E6EB, 0x393A41),
        "LineControl": (0x868B95, 0x8E929C), "Text": (0x17191F, 0xF3F4F6), "Text2": (0x50545E, 0xC4C7CE),
        "Text3": (0x62666F, 0xA9ADB5), "Disabled": (0xA7ABB3, 0x62656D), "DisabledFill": (0xF3F4F6, 0x2E2F35),
        "Critical": (0xC42B1C, 0xFF99A4), "Caution": (0x935600, 0xF2B544), "TipFill": (0x17191F, 0xF3F4F6),
        "TipText": (0xFFFFFF, 0x17191F), "Smoke": (0x17191F, 0x000000), "Accent": (0x2F6BEB, 0x82AAFF),
        "AccentHover": (0x295FD4, 0x98B9FF), "AccentPressed": (0x2453BB, 0x6E97EE), "OnAccent": (0xFFFFFF, 0x0D1A3A),
        "AccentTint": (0xE6EEFE, 0x263354), "AccentText": (0x2559CC, 0x98B9FF),
    ]

    func hex(_ name: String) -> UInt32 {
        let table = skin == .pixel ? Palette.pixel : Palette.classic
        // Pixel has no AccentText or hairlines; fall back to the nearest token.
        guard let pair = table[name] ?? table["Text"] else { return 0 }
        return dark ? pair.1 : pair.0
    }

    func color(_ name: String) -> Color { Color(hex: hex(name)) }

    /// Classic's translucent edges.
    var hairline: Color { dark ? Color.white.opacity(0.07) : Color(hex: 0x17191F).opacity(0.08) }
    var hairlineLip: Color { dark ? Color.black.opacity(0.35) : Color(hex: 0x17191F).opacity(0.13) }

    /// An agent's tint: its colour over paper, 14 % (Classic) or 18 % (Pixel) in light, 24 % / 25 % in dark.
    func tint(_ swatch: UInt32) -> Color {
        let share: Double = skin == .pixel ? (dark ? 0.25 : 0.18) : (dark ? 0.24 : 0.14)
        let paper = hex("Paper")
        func mix(_ shift: UInt32) -> Double {
            let over = Double((swatch >> shift) & 0xFF), under = Double((paper >> shift) & 0xFF)
            return (over * share + under * (1 - share)) / 255
        }
        return Color(red: mix(16), green: mix(8), blue: mix(0))
    }

    /// An agent's colour as a bar, a chip or a chart wears it: Pixel's
    /// hand-tuned pair for a default look, else (and for Classic) the look's
    /// swatch pulled into the readable band (PixelTokens.Agent, LookCatalog.OnPaper).
    func agentInk(id: String, swatch: UInt32, defaultLook: Bool) -> Color {
        guard skin == .pixel, defaultLook else { return readable(swatch) }
        let pairs: [String: (UInt32, UInt32)] = [
            "claude": (0xC2603F, 0xE8916E), "codex": (0x6E717C, 0xB9BCC6), "antigravity": (0x2F6BE0, 0x7FA6FF),
            "grok": (0x2B2B33, 0xD9D9E0), "kimi": (0x1F72E0, 0x6FB2FF),
        ]
        let pair = pairs[id] ?? (0x4058E8, 0x8EA0FF)
        return Color(hex: dark ? pair.1 : pair.0)
    }

    /// Brand.Readable: the character's colour made readable on this surface.
    func readable(_ swatch: UInt32) -> Color {
        var r = Double((swatch >> 16) & 0xFF), g = Double((swatch >> 8) & 0xFF), b = Double(swatch & 0xFF)
        func luma() -> Double { 0.2126 * r + 0.7152 * g + 0.0722 * b }
        if !dark && luma() > 118 {
            let k = 118 / luma(); r *= k; g *= k; b *= k
        } else if dark && luma() < 165 {
            let share = (165 - luma()) / (255 - luma())
            r += (255 - r) * share; g += (255 - g) * share; b += (255 - b) * share
        }
        return Color(red: r / 255, green: g / 255, blue: b / 255)
    }
}

extension Color {
    init(hex: UInt32, opacity: Double = 1) {
        self.init(.sRGB, red: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255,
                  blue: Double(hex & 0xFF) / 255, opacity: opacity)
    }
}

/// The pixel face: Ark Pixel's 10-pixel Latin design (SIL OFL 1.1, licence
/// shipped beside it). Registered from the app's Resources/Fonts.
enum PixelFace {
    private(set) static var family: String?

    static func register() {
        guard family == nil else { return }
        let folder = Bundle.main.resourceURL?.appendingPathComponent("Fonts")
        let url = (ProcessInfo.processInfo.environment["EDGELINGS_FONTS"].map { URL(fileURLWithPath: $0) } ?? folder)?
            .appendingPathComponent("ark-pixel-10px-proportional-latin.ttf")
        guard let url else { return }
        var error: Unmanaged<CFError>?
        _ = CTFontManagerRegisterFontsForURL(url as CFURL, .process, &error)
        if let descriptors = CTFontManagerCreateFontDescriptorsFromURL(url as CFURL) as? [CTFontDescriptor], let first = descriptors.first,
           let name = CTFontDescriptorCopyAttribute(first, kCTFontNameAttribute) as? String {
            family = name
        }
    }

    /// The screen's scale (the offscreen renders draw at 2).
    static var scale: CGFloat = NSScreen.main?.backingScaleFactor ?? 2

    /// A size that keeps the face's pixels whole (PixelPlate.FontSize): each
    /// of its 10-pixel design's pixels covers a whole number of screen
    /// pixels, as near to `dip` as that allows. Every line of a pixel panel
    /// that is body-sized or smaller sets in the body size, as on Windows.
    static func size(_ dip: CGFloat = 13) -> CGFloat {
        let target = max(dip, 13)
        let perPixel = max(1, (target * scale / 10).rounded())
        return 10 * perPixel / scale
    }

    static func font(_ dip: CGFloat = 13) -> Font {
        register()
        if let family { return Font.custom(family, size: size(dip)) }
        return Font.system(size: size(dip), design: .monospaced)
    }
}

/// A pixel-art panel's outline: a rectangle whose corners step in by `steps` cells of 2 pt.
struct SteppedRect: Shape {
    var steps: Int
    var cell: CGFloat = 2

    func path(in rect: CGRect) -> Path {
        let c = cell
        let l = rect.minX, t = rect.minY, r = rect.maxX, b = rect.maxY
        var points: [CGPoint] = []
        for i in 0..<steps {
            points.append(CGPoint(x: r - CGFloat(steps - i) * c, y: t + CGFloat(i) * c))
            points.append(CGPoint(x: r - CGFloat(steps - i) * c, y: t + CGFloat(i + 1) * c))
        }
        points.append(CGPoint(x: r, y: t + CGFloat(steps) * c))
        points.append(CGPoint(x: r, y: b - CGFloat(steps) * c))
        for i in 0..<steps {
            points.append(CGPoint(x: r - CGFloat(i + 1) * c, y: b - CGFloat(steps - i) * c))
            points.append(CGPoint(x: r - CGFloat(i + 1) * c, y: b - CGFloat(steps - i - 1) * c))
        }
        for i in 0..<steps {
            points.append(CGPoint(x: l + CGFloat(steps - i) * c, y: b - CGFloat(i) * c))
            points.append(CGPoint(x: l + CGFloat(steps - i) * c, y: b - CGFloat(i + 1) * c))
        }
        points.append(CGPoint(x: l, y: b - CGFloat(steps) * c))
        points.append(CGPoint(x: l, y: t + CGFloat(steps) * c))
        for i in 0..<steps {
            points.append(CGPoint(x: l + CGFloat(i + 1) * c, y: t + CGFloat(steps - i) * c))
            points.append(CGPoint(x: l + CGFloat(i + 1) * c, y: t + CGFloat(steps - i - 1) * c))
        }
        var path = Path()
        path.addLines(points)
        path.closeSubpath()
        return path
    }
}

/// The pixel plate: a one-cell outline around a flat fill, corners stepped,
/// and optionally a pointer in the outline colour, flush with one edge: three
/// cells against it, then one cell beyond (PixelPlate.Pointer). pointerY is
/// the pointer's centre measured from the plate's top; the pointer sits on the
/// right edge unless pointLeft.
struct PixelPlateBackground: View {
    let palette: Palette
    var outline: Color?
    var fill: Color?
    var pointerY: CGFloat?
    var pointLeft = false

    var body: some View {
        let line = outline ?? palette.color("Line")
        ZStack {
            PlateOutline(pointerY: pointerY, pointLeft: pointLeft).fill(line)
            SteppedRect(steps: 1).fill(fill ?? palette.color("Paper")).padding(2)
        }
    }
}

struct PlateOutline: Shape {
    var pointerY: CGFloat?
    var pointLeft: Bool

    func path(in rect: CGRect) -> Path {
        var path = SteppedRect(steps: 2).path(in: rect)
        guard let centre = pointerY else { return path }
        let c: CGFloat = 2
        let row = min(max((centre / c - 0.5).rounded() * c, 3 * c), max(3 * c, rect.height - 5 * c)) + rect.minY
        let edge = pointLeft ? rect.minX : rect.maxX
        let one = pointLeft ? -c : c
        let two = pointLeft ? -2 * c : 2 * c
        let points = [CGPoint(x: edge, y: row - c), CGPoint(x: edge + one, y: row - c), CGPoint(x: edge + one, y: row),
                      CGPoint(x: edge + two, y: row), CGPoint(x: edge + two, y: row + c), CGPoint(x: edge + one, y: row + c),
                      CGPoint(x: edge + one, y: row + 2 * c), CGPoint(x: edge, y: row + 2 * c)]
        path.addLines(points)
        path.closeSubpath()
        return path
    }
}

/// A pixel drawing: each character of a row is a cell, `.` is empty.
struct PixelGridView: View {
    let rows: [String]
    let colors: [Character: UInt32]
    var cell: CGFloat = 2
    /// One ink for every filled cell (the icons), when set.
    var ink: Color?

    var body: some View {
        Canvas { context, _ in
            for (y, row) in rows.enumerated() {
                for (x, key) in row.enumerated() where key != "." && key != " " {
                    let color: Color
                    if let ink { color = ink } else if let value = colors[key] { color = Color(hex: value) } else { continue }
                    context.fill(Path(CGRect(x: CGFloat(x) * cell, y: CGFloat(y) * cell, width: cell, height: cell)), with: .color(color))
                }
            }
        }
        .frame(width: CGFloat(rows.map { $0.count }.max() ?? 10) * cell, height: CGFloat(rows.count) * cell)
    }
}

/// A Pixel kit icon by name, in one ink.
struct PixelIcon: View {
    let name: String
    let ink: Color
    var cell: CGFloat = 2

    var body: some View {
        PixelGridView(rows: PixelArt.icons[name] ?? [], colors: [:], cell: cell, ink: ink)
            .frame(width: 10 * cell, height: 10 * cell, alignment: .topLeading)
    }
}

/// An agent's face: Pixel's 10 x 10 art for the look it wears, or Classic's
/// disc in the look's colour with the app's two dot eyes (FaceChip).
struct AgentFace: View {
    let id: String
    let look: String
    let swatch: UInt32
    let skin: Skin
    var size: CGFloat = 20

    var body: some View {
        if skin == .pixel {
            let grid = PixelArt.lookFaces[id + "/" + look] ?? PixelArt.faces[id] ?? PixelArt.logo
            PixelGridView(rows: grid.rows, colors: grid.colors, cell: size / 10)
        } else {
            FaceChip(swatch: swatch, size: size)
        }
    }
}

/// Classic's face: a disc in the agent's colour with two eyes, white or, on a
/// disc too light for white, ink.
struct FaceChip: View {
    let swatch: UInt32
    var size: CGFloat = 16

    var body: some View {
        let r: Double = Double((swatch >> 16) & 0xFF)
        let g: Double = Double((swatch >> 8) & 0xFF)
        let b: Double = Double(swatch & 0xFF)
        let luma: Double = 0.2126 * r + 0.7152 * g + 0.0722 * b
        let eyes: Color = luma > 170 ? Color(hex: 0x17191F) : Color.white
        ZStack {
            Circle().fill(Color(hex: swatch))
            HStack(spacing: size * 0.22) {
                Capsule().fill(eyes).frame(width: size * 0.15, height: size * 0.26)
                Capsule().fill(eyes).frame(width: size * 0.15, height: size * 0.26)
            }
            .offset(y: -size * 0.04)
        }
        .frame(width: size, height: size)
    }
}

/// Classic's soft tail (PaperPlate): a rounded triangle, half-base 10 and
/// reach 9, in the card's own fill so the two read as one shape. rect is the
/// strip beside the plate's edge; the tip is on the outer side.
struct SoftTail: Shape {
    var pointsRight: Bool
    var halfBase: CGFloat = 10
    var reach: CGFloat = 9

    func path(in rect: CGRect) -> Path {
        let edgeX = pointsRight ? rect.minX : rect.maxX
        let tipX = pointsRight ? edgeX + reach : edgeX - reach
        let sign: CGFloat = pointsRight ? 1 : -1
        let mid = rect.midY
        var path = Path()
        path.move(to: CGPoint(x: edgeX, y: mid - halfBase))
        path.addQuadCurve(to: CGPoint(x: tipX, y: mid - 1.2), control: CGPoint(x: edgeX + 3 * sign, y: mid - 2))
        path.addQuadCurve(to: CGPoint(x: tipX, y: mid + 1.2), control: CGPoint(x: tipX + 1.5 * sign, y: mid))
        path.addQuadCurve(to: CGPoint(x: edgeX, y: mid + halfBase), control: CGPoint(x: edgeX + 3 * sign, y: mid + 2))
        path.closeSubpath()
        return path
    }
}

/// A quota bar in pixel blocks: the shown share fills whole blocks over a
/// track tinted with the same colour.
struct PixelBar: View {
    let share: Double?
    let color: Color
    let blocks: Int

    var body: some View {
        let filled = share.map { s in min(max(Int((s * Double(blocks)).rounded()), s > 0 ? 1 : 0), blocks) } ?? 0
        HStack(spacing: 2) {
            ForEach(0..<blocks, id: \.self) { index in
                Rectangle().fill(index < filled ? color : color.opacity(0.25)).frame(width: 4, height: 6)
            }
        }
        .opacity(share == nil ? 0.5 : 1)
    }
}

/// Classic's quota bar: a rounded track with the share filled in.
struct ClassicBar: View {
    let share: Double?
    let color: Color
    let track: Color

    var body: some View {
        GeometryReader { proxy in
            ZStack(alignment: .leading) {
                Capsule().fill(track)
                Capsule().fill(color).frame(width: max(share.map { $0 > 0 ? 4 : 0 } ?? 0, proxy.size.width * CGFloat(min(max(share ?? 0, 0), 1))))
            }
        }
        .frame(height: 6)
        .opacity(share == nil ? 0.5 : 1)
    }
}
