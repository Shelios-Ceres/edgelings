// `Edgelings --render <folder>`: draws the app's screens offscreen from
// synthetic data and writes PNGs, so the design chats can check them next to
// the Windows renders and CI can prove the drawing works with no screen. No
// Core, no agent, no network and nothing of the user's is read.
import AppKit
import SwiftUI

enum RenderMode {
    @MainActor
    static func run(into folder: String) {
        let url = URL(fileURLWithPath: folder)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        PixelFace.register()
        NSLog("render: pixel face \(PixelFace.family ?? "missing")")
        var written = 0
        func save(_ image: NSImage?, _ name: String) {
            guard let image, let tiff = image.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) else {
                NSLog("render: \(name) failed")
                return
            }
            try? png.write(to: url.appendingPathComponent(name))
            written += 1
        }

        for theme in ["pixel", "classic"] {
            save(botSheet(theme: theme), "bots-\(theme).png")
        }
        for skin in [Skin.pixel, Skin.classic] {
            for dark in [false, true] {
                let tag = "\(skin == .pixel ? "pixel" : "classic")-\(dark ? "dark" : "light")"
                let palette = Palette(skin: skin, dark: dark)
                save(snapshot(scene(palette: palette, cards: Sample.cards(Array(Sample.all.prefix(1))), columns: [[0]], bubble: "Done — your turn!"), dark: dark), "scene-one-\(tag).png")
                save(snapshot(scene(palette: palette, cards: Sample.cards(Sample.all), columns: [[0, 1], [2, 3]], bubble: nil), dark: dark), "scene-all-\(tag).png")
                save(snapshot(bubbles(palette: palette), dark: dark), "bubbles-\(tag).png")
            }
        }
        NSLog("render: wrote \(written) images to \(folder)")
    }

    // MARK: SwiftUI → PNG

    @MainActor
    private static func snapshot<V: View>(_ view: V, dark: Bool) -> NSImage? {
        let backdrop = dark ? Color(hex: 0x1C1C20) : Color(hex: 0xC9CDD4)
        let framed = ZStack { backdrop; view }.fixedSize()
        let renderer = ImageRenderer(content: framed)
        renderer.scale = 2
        return renderer.nsImage
    }

    // MARK: Scenes

    /// A bit of desktop: the card (or cards) beside the drawer column, a bubble beside the top character.
    private static func scene(palette: Palette, cards: CardsModel, columns: [[Int]], bubble: String?) -> some View {
        let pixel = palette.skin == .pixel
        return HStack(alignment: .center, spacing: 0) {
            CardView(model: cards, palette: palette, columns: columns, tailY: 60, rightEdge: true)
            if let bubble {
                BubbleView(line: bubble, kind: "done", swatch: 0xD97757, palette: palette, rightEdge: true, tailY: 22)
            }
            DrawerColumn(pixel: pixel, count: min(cards.cards.count == 1 ? 4 : 6, 6))
        }
        .padding(24)
    }

    private static func bubbles(palette: Palette) -> some View {
        VStack(alignment: .trailing, spacing: 6) {
            BubbleView(line: "Done — your turn!", kind: "done", swatch: 0xD97757, palette: palette, rightEdge: true, tailY: 22)
            BubbleView(line: "Can I run this? Needs your OK.", kind: "waiting", swatch: 0x5AA8F0, palette: palette, rightEdge: true, tailY: 22)
            BubbleView(line: "3 sessions need you.", kind: "waiting", swatch: 0x4285F4, palette: palette, rightEdge: true, tailY: 22)
            BubbleView(line: "Something broke…", kind: "error", swatch: 0xE05C4B, palette: palette, rightEdge: true, tailY: 22)
        }
        .padding(16)
    }

    // MARK: The characters

    /// Every state of every character in a set, as the engine draws it after a short run.
    @MainActor
    private static func botSheet(theme: String) -> NSImage? {
        guard let engine = try? CharacterEngine(theme: theme, directory: AppController.enginesDirectory()) else {
            NSLog("render: engine \(theme) did not load")
            return nil
        }
        let kinds = ["claude", "codex", "antigravity", "grok", "kimi", "deepseek"]
        let states = ["idle", "thinking", "reading", "writing", "running", "asking", "approving", "finished", "failed", "offline", "sleeping", "resting"]
        var indexes: [Int] = []
        for (position, kind) in kinds.enumerated() {
            indexes.append(engine.addBot(kind: kind, tempo: 1, seed: 7 + position * 13, look: ""))
        }
        let cell = 52
        let scale = 2
        let width = (states.count + 1) * cell * scale, height = kinds.count * cell * scale
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4,
                                  space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setFillColor(CGColor(srgbRed: 0.93, green: 0.92, blue: 0.9, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        let canvas = SvgCanvas()
        for (column, state) in states.enumerated() {
            for index in indexes where engine.draws(state) || state == "idle" { engine.setState(index, state, phase: 0) }
            var frames: [EngineFrame?] = []
            for _ in 0..<8 { frames = engine.tick([Double?](repeating: 0.1, count: kinds.count), size: 44) }
            for (row, index) in indexes.enumerated() {
                guard let frame = frames[index] else { continue }
                ctx.saveGState()
                ctx.translateBy(x: CGFloat((column + 1) * cell * scale + 4 * scale), y: CGFloat((kinds.count - row) * cell * scale - 4 * scale))
                ctx.scaleBy(x: CGFloat(scale), y: -CGFloat(scale))
                try? canvas.render(svg: frame.svg, into: ctx, antialias: theme != "pixel")
                ctx.restoreGState()
            }
        }
        guard let image = ctx.makeImage() else { return nil }
        return NSImage(cgImage: image, size: NSSize(width: width / scale, height: height / scale))
    }
}

/// The drawer column of round characters, each in its default look at rest.
struct DrawerColumn: View {
    let pixel: Bool
    let count: Int

    var body: some View {
        let kinds = ["claude", "codex", "antigravity", "grok", "kimi", "deepseek"]
        VStack(spacing: 10) {
            ForEach(0..<min(count, kinds.count), id: \.self) { index in
                if let image = DrawerColumn.bot(kind: kinds[index], pixel: pixel, index: index) {
                    Image(decorative: image, scale: 2).interpolation(pixel ? .none : .high).frame(width: 44, height: 44)
                } else {
                    Circle().fill(Color.gray).frame(width: 44, height: 44)
                }
            }
        }
        .padding(.vertical, 12).padding(.horizontal, 6)
    }

    static func bot(kind: String, pixel: Bool, index: Int) -> CGImage? {
        guard let engine = try? CharacterEngine(theme: pixel ? "pixel" : "classic", directory: AppController.enginesDirectory()) else { return nil }
        let bot = engine.addBot(kind: kind, tempo: 1, seed: 7 + index * 13, look: "")
        var svg: String?
        for _ in 0..<6 { svg = engine.tick([Double?](repeating: 0.1, count: 1), size: 44).first.flatMap { $0 }?.svg }
        guard let svg else { return nil }
        _ = bot
        let side = 88
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.translateBy(x: 0, y: CGFloat(side))
        ctx.scaleBy(x: 2, y: -2)
        try? SvgCanvas().render(svg: svg, into: ctx, antialias: !pixel)
        return ctx.makeImage()
    }
}

/// Synthetic cards, as Core would send them.
enum Sample {
    static func cards(_ list: [CardModel]) -> CardsModel { CardsModel(pixel: false, cards: list) }

    static let claude = CardModel(
        id: "claude", name: "Claude", swatch: 0xD97757, phase: "Running commands", detail: "Bash", plan: "MAX",
        tasks: TasksModel(label: "Tasks", text: "2 running", number: "2"),
        meters: [MeterModel(label: "5h", value: "38%", shown: 0.38, used: 0.38, resets: "Resets in 2h 10m"),
                 MeterModel(label: "Week", value: "12%", shown: 0.12, used: 0.12, resets: nil)],
        tokens: TokensModel(label: "Tokens", today: "today 1.2M", todayNumber: "1.2M", month: "48M this month", monthNumber: "48M"),
        note: [], waiting: WaitingModel(more: 0, rows: []))

    static let codex = CardModel(
        id: "codex", name: "Codex", swatch: 0x9EA3B0, phase: "Waiting for your approval", detail: nil, plan: nil, tasks: nil,
        meters: [MeterModel(label: "5h", value: "93%", shown: 0.93, used: 0.93, resets: nil),
                 MeterModel(label: "Week", value: "61%", shown: 0.61, used: 0.61, resets: nil)],
        tokens: TokensModel(label: "Tokens", today: "today 310K", todayNumber: "310K", month: "9.8M this month", monthNumber: "9.8M"),
        note: [PhraseModel(text: "2 reset cards", lead: nil, numbers: ["2"])],
        waiting: WaitingModel(more: 1, rows: [WaitingRowModel(title: "A session", word: "Approval", kind: "approval"),
                                              WaitingRowModel(title: "Another session", word: "Question", kind: "question")]))

    static let antigravity = CardModel(
        id: "antigravity", name: "Antigravity", swatch: 0x4285F4, phase: "Idle", detail: nil, plan: nil, tasks: nil,
        meters: [MeterModel(label: "5h", value: "20%", shown: 0.2, used: 0.2, resets: nil),
                 MeterModel(label: "Week", value: "—", shown: nil, used: nil, resets: "Awaiting sync"),
                 MeterModel(label: "Argon", value: "7%", shown: 0.07, used: 0.07, resets: nil)],
        tokens: TokensModel(label: "Tokens", today: "Not scanned yet", todayNumber: "", month: "—", monthNumber: ""),
        note: [PhraseModel(text: "Quota updated 12 min ago", lead: nil, numbers: ["12"])], waiting: WaitingModel(more: 0, rows: []))

    static let kimi = CardModel(
        id: "kimi", name: "Kimi Code", swatch: 0x2E8FF7, phase: "Sleeping", detail: "In the tray", plan: nil, tasks: nil,
        meters: [MeterModel(label: "Month", value: "44%", shown: 0.44, used: 0.44, resets: nil)],
        tokens: TokensModel(label: "Tokens", today: "today 0", todayNumber: "0", month: "3.1M this month", monthNumber: "3.1M"),
        note: [PhraseModel(text: "Kimi Code 30% · Kimi app 14%", lead: "Month:", numbers: ["30%", "14%"])],
        waiting: WaitingModel(more: 0, rows: []))

    static let all = [claude, codex, antigravity, kimi]
}
