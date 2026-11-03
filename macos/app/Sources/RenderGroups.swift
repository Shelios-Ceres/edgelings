// Task 4's sample films from the Mac drawer's own pipeline: each agent's character plays a
// realistic stretch of a session (the design chats' timelines, modelled on the 21-day replay)
// through Core's GroupRotation on a fake clock and the real engine, and a frame is kept each
// second. One contact sheet per style and theme: a block per agent, one cell per second, the
// phase named where it changes and the state the bot plays under every frame.
import SwiftUI
import AppKit

enum RenderGroups {
    struct Cell {
        let second: Int
        let phase: String
        let state: String
        let image: CGImage
    }

    struct Block {
        let kind: String
        let look: String
        let cells: [Cell]
    }

    static let kinds = ["claude", "codex", "antigravity", "grok", "kimi", "deepseek"]

    // [phase, seconds] per agent, as in each design chat's sample page.
    static let pixelTurns: [String: [(String, Double)]] = [
        "claude": [("thinking", 14), ("running", 6), ("streaming", 4), ("thinking", 18), ("reading", 3), ("writing", 5), ("running", 16), ("streaming", 6), ("finished", 4)],
        "codex": [("thinking", 26), ("streaming", 10), ("running", 8), ("thinking", 30), ("streaming", 12), ("finished", 4)],
        "antigravity": [("thinking", 8), ("running", 10), ("writing", 12), ("reading", 6), ("thinking", 10), ("web", 8), ("finished", 4)],
        "grok": [("thinking", 16), ("searching", 6), ("running", 12), ("streaming", 14), ("finished", 4)],
        "kimi": [("thinking", 43), ("running", 2), ("thinking", 30), ("spawning", 12), ("thinking", 20), ("finished", 4)],
        "deepseek": [("thinking", 22), ("writing", 8), ("thinking", 34), ("running", 6), ("finished", 4)],
    ]

    static let classicTurns: [String: [(String, Double)]] = [
        "claude": [("thinking", 18), ("streaming", 4), ("running", 9), ("thinking", 12), ("reading", 2), ("writing", 3), ("streaming", 5), ("running", 14), ("thinking", 22), ("streaming", 6), ("finished", 7)],
        "codex": [("thinking", 34), ("streaming", 8), ("running", 6), ("thinking", 26), ("writing", 2), ("streaming", 10), ("running", 5), ("thinking", 8), ("finished", 7)],
        "kimi": [("thinking", 43), ("running", 2), ("thinking", 18), ("spawning", 2), ("orbit", 14), ("thinking", 12), ("writing", 2), ("streaming", 4), ("finished", 7)],
        "antigravity": [("running", 8), ("reading", 4), ("thinking", 6), ("writing", 10), ("searching", 4), ("running", 12), ("thinking", 8), ("controlling", 6), ("web", 4), ("reading", 3), ("writing", 6), ("thinking", 4), ("running", 10), ("finished", 7)],
        "grok": [("thinking", 25), ("web", 6), ("streaming", 8), ("thinking", 15), ("searching", 4), ("streaming", 6), ("thinking", 14), ("streaming", 5), ("finished", 7)],
        "deepseek": [("thinking", 20), ("streaming", 6), ("running", 12), ("writing", 6), ("thinking", 18), ("streaming", 8), ("running", 6), ("thinking", 10), ("finished", 7)],
    ]

    /// The AgentPhase a sample turn's word stands for.
    static func agentPhase(_ word: String) -> String {
        switch word {
        case "thinking": return "Thinking"
        case "streaming": return "Streaming"
        case "reading": return "Reading"
        case "searching": return "Searching"
        case "web": return "WebSearching"
        case "writing": return "Writing"
        case "running": return "Running"
        case "controlling": return "Controlling"
        case "spawning": return "Spawning"
        case "orbit": return "WaitingOnAgents"
        default: return "Finished"
        }
    }

    /// Colour of a phase's group, for the bar under a cell (the agents with a Busy group show Think and Do alike).
    static func groupColor(_ word: String, busy: Bool) -> Color {
        switch word {
        case "thinking", "streaming", "compacting": return busy ? Color(hex: 0xC2448A) : Color(hex: 0x6B4EE6)
        case "reading", "searching", "web", "writing", "running", "controlling", "working": return busy ? Color(hex: 0xC2448A) : Color(hex: 0xD9822B)
        case "spawning", "orbit", "sending": return Color(hex: 0x2E9E8F)
        default: return Color(hex: 0x9A97A3)
        }
    }

    private static func image(of svg: String, pixel: Bool) -> CGImage? {
        let side = 88
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.translateBy(x: 0, y: CGFloat(side))
        ctx.scaleBy(x: 2, y: -2)
        try? SvgCanvas().render(svg: svg, into: ctx, antialias: !pixel)
        return ctx.makeImage()
    }

    /// Plays every agent's turn through Core's rotation and the engine; a frame each second.
    static func simulate(theme: String) -> [Block]? {
        guard let bridge = AppController.shared.bridge,
              let engine = try? CharacterEngine(theme: theme, directory: AppController.enginesDirectory()) else { return nil }
        let pixel = theme == "pixel"
        let skin: Skin = pixel ? .pixel : .classic
        let turns = pixel ? pixelTurns : classicTurns
        var result: [Block] = []
        for (position, kind) in kinds.enumerated() {
            guard let turn = turns[kind] else { continue }
            let look = SampleLooks.look(kind, skin).id
            let index = engine.addBot(kind: kind, tempo: 1, seed: 7 + position * 13, look: look)
            let states = (bridge.request("rotationStates", ["theme": theme, "kind": kind, "look": look])["states"] as? [String]) ?? []
            let cycles = engine.cycles(kind: kind, look: look, states: states, tempo: 1)
            bridge.request("rotationSetup", ["theme": theme, "bots": [["id": kind, "kind": kind, "look": look, "cycles": cycles] as [String: Any]]])
            let total = turn.reduce(0) { $0 + $1.1 }
            let step = 0.1
            var applied = ""
            var cells: [Cell] = []
            var shot = 0
            var t = 0.0
            while t < total {
                var end = 0.0
                var word = turn.last!.0
                for (name, seconds) in turn {
                    end += seconds
                    if t < end - 1e-9 { word = name; break }
                }
                let reply = bridge.request("rotate", ["now": t, "reduced": false, "bots": [["id": kind, "phase": agentPhase(word)]]])
                if let answer = (reply["bots"] as? [[String: Any]])?.first, let state = answer["state"] as? String, state != applied {
                    applied = state
                    engine.setState(index, state, phase: 0)
                }
                var dts = [Double?](repeating: nil, count: index + 1)
                dts[index] = step
                if let produced = engine.tick(dts, size: 44)[index] {
                    // A self-completing state (finished) settles into the set's resting pose, as the drawer's hold does.
                    if produced.state == "idle" && applied != "idle" {
                        applied = engine.draws("listening") ? "listening" : "idle"
                        engine.setState(index, applied, phase: -1)
                    }
                    if Double(shot) <= t + 1e-9, let picture = image(of: produced.svg, pixel: pixel) {
                        cells.append(Cell(second: shot, phase: word, state: produced.state, image: picture))
                        shot += 1
                    }
                }
                t += step
            }
            result.append(Block(kind: kind, look: look, cells: cells))
        }
        return result
    }

    private struct CellView: View {
        let cell: Cell
        let named: Bool
        let busy: Bool
        let dark: Bool

        var body: some View {
            VStack(spacing: 1) {
                Text(named ? cell.phase : " ").font(.system(size: 8, weight: .semibold)).lineLimit(1)
                Image(decorative: cell.image, scale: 2).resizable().interpolation(.none).frame(width: 44, height: 44)
                Rectangle().fill(RenderGroups.groupColor(cell.phase, busy: busy)).frame(height: 3)
                Text(cell.state).font(.system(size: 8)).lineLimit(1).minimumScaleFactor(0.7)
                Text("\(cell.second) s").font(.system(size: 7)).foregroundColor(.secondary)
            }
            .frame(width: 50)
        }
    }

    @MainActor
    static func sheet(theme: String, dark: Bool, blocks: [Block]) -> NSImage? {
        let perRow = 20
        let ink = dark ? Color.white : Color.black
        let view = VStack(alignment: .leading, spacing: 18) {
            Text("Work-state groups on the Mac drawer (\(theme)): Core's rotation on a fake clock, the real engine, a frame each second. Bar colour: Think violet, Do orange, Team green, Busy magenta, other grey.")
                .font(.system(size: 11)).frame(width: 1000, alignment: .leading)
            ForEach(blocks, id: \.kind) { block in
                VStack(alignment: .leading, spacing: 4) {
                    Text("\(block.kind) · \(block.look)").font(.system(size: 13, weight: .semibold))
                    let busy = block.kind == "kimi" || block.kind == "antigravity"
                    ForEach(0..<((block.cells.count + perRow - 1) / perRow), id: \.self) { row in
                        HStack(spacing: 0) {
                            ForEach(Array(block.cells[(row * perRow)..<min(block.cells.count, (row + 1) * perRow)].enumerated()), id: \.offset) { offset, cell in
                                let previous = row * perRow + offset > 0 ? block.cells[row * perRow + offset - 1].phase : ""
                                CellView(cell: cell, named: cell.phase != previous, busy: busy, dark: dark)
                            }
                        }
                    }
                }
            }
        }
        .foregroundColor(ink)
        .padding(16)
        .background(dark ? Color(red: 0.11, green: 0.11, blue: 0.125) : Color(red: 0.94, green: 0.93, blue: 0.9))
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        return renderer.nsImage
    }

    @MainActor
    static func run(save: (NSImage?, String) -> Void) {
        for theme in ["pixel", "classic"] {
            guard let blocks = simulate(theme: theme) else {
                NSLog("render: work groups \(theme) could not be simulated")
                continue
            }
            for dark in [false, true] {
                save(sheet(theme: theme, dark: dark, blocks: blocks), "groups-\(theme)-\(dark ? "dark" : "light").png")
            }
        }
    }
}
