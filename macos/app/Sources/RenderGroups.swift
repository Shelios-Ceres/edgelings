// Task 4's films from the Mac drawer's own pipeline, built as the Windows films are
// (DrawerWindow.GroupsFilm.cs) so the traces can be diffed line by line: every look of every
// agent plays its style's sample turn twice through the real engine, once as the drawer played it
// before the groups (every phase its own state) and once through Core's GroupRotation, on a
// 1/30 s clock; a frame is kept every two seconds, and groups-trace-<style>.txt lists each change
// of state. `Edgelings --render-groups <folder>`.
import SwiftUI
import AppKit

enum RenderGroups {
    static let frameEvery = 2.0
    static let kinds = ["claude", "codex", "antigravity", "grok", "kimi", "deepseek"]

    // [phase word, seconds] per agent, as in each design chat's sample page.
    static let pixelTurns: [String: String] = [
        "claude": "thinking 14, running 6, streaming 4, thinking 18, reading 3, writing 5, running 16, streaming 6, finished 4",
        "codex": "thinking 26, streaming 10, running 8, thinking 30, streaming 12, finished 4",
        "antigravity": "thinking 8, running 10, writing 12, reading 6, thinking 10, web 8, finished 4",
        "grok": "thinking 16, searching 6, running 12, streaming 14, finished 4",
        "kimi": "thinking 43, running 2, thinking 30, spawning 12, thinking 20, finished 4",
        "deepseek": "thinking 22, writing 8, thinking 34, running 6, finished 4",
    ]

    static let classicTurns: [String: String] = [
        "claude": "thinking 18, streaming 4, running 9, thinking 12, reading 2, writing 3, streaming 5, running 14, thinking 22, streaming 6, finished 7",
        "codex": "thinking 34, streaming 8, running 6, thinking 26, writing 2, streaming 10, running 5, thinking 8, finished 7",
        "kimi": "thinking 43, running 2, thinking 18, spawning 2, orbit 14, thinking 12, writing 2, streaming 4, finished 7",
        "antigravity": "running 8, reading 4, thinking 6, writing 10, searching 4, running 12, thinking 8, controlling 6, web 4, reading 3, writing 6, thinking 4, running 10, finished 7",
        "grok": "thinking 25, web 6, streaming 8, thinking 15, searching 4, streaming 6, thinking 14, streaming 5, finished 7",
        "deepseek": "thinking 20, streaming 6, running 12, writing 6, thinking 18, streaming 8, running 6, thinking 10, finished 7",
    ]

    struct Frame {
        let at: Double
        let phase: String
        let state: String
        let image: CGImage
    }

    private static func turn(_ text: String) -> [(phase: String, seconds: Double)] {
        text.components(separatedBy: ",").map { part in
            let pair = part.trimmingCharacters(in: .whitespaces).components(separatedBy: " ")
            let phase: String
            switch pair[0] {
            case "web": phase = "WebSearching"
            case "orbit": phase = "WaitingOnAgents"
            default: phase = pair[0].prefix(1).uppercased() + pair[0].dropFirst()
            }
            return (phase, Double(pair[1]) ?? 0)
        }
    }

    private static func phaseAt(_ turn: [(phase: String, seconds: Double)], _ time: Double) -> String {
        var t = time
        for part in turn {
            if t + 1e-9 < part.seconds { return part.phase }
            t -= part.seconds
        }
        return turn.last!.phase
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

    private static func padded(_ text: String, _ width: Int) -> String {
        text.count >= width ? text : text + String(repeating: " ", count: width - text.count)
    }

    /// Plays one look's turn, today or grouped; appends its trace lines and returns the kept frames.
    private static func play(engine: CharacterEngine, index: Int, count: Int, theme: String, id: String, turn: [(phase: String, seconds: Double)],
                             grouped: Bool, label: String, trace: inout String, pixel: Bool) -> [Frame] {
        guard let bridge = AppController.shared.bridge else { return [] }
        engine.setState(index, "idle", phase: 0)
        var playing: String?
        var frames: [Frame] = []
        let total = turn.reduce(0) { $0 + $1.seconds }
        var dts = [Double?](repeating: nil, count: count)
        dts[index] = 1.0 / 30.0
        var nextFrame = frameEvery / 2
        trace += "== \(label) \(grouped ? "grouped" : "today")\n"
        var t = 0.0
        while t < total {
            let phase = phaseAt(turn, t)
            let reply = bridge.request("rotate", ["theme": theme, "today": !grouped, "now": t, "reduced": false, "bots": [["id": id, "phase": phase]]])
            guard let state = ((reply["bots"] as? [[String: Any]])?.first)?["state"] as? String else { t += 1.0 / 30.0; continue }
            if state != playing {
                trace += String(format: "%6.1f", t) + " s  " + padded(phase, 16) + " -> " + state + "\n"
                engine.setState(index, state, phase: 0)
                playing = state
            }
            if t >= nextFrame {
                if let produced = engine.tick(dts, size: 44)[index], let picture = image(of: produced.svg, pixel: pixel) {
                    frames.append(Frame(at: t, phase: phase, state: playing ?? "", image: picture))
                    nextFrame += frameEvery
                }
            } else {
                engine.benchAdvance(dts)
            }
            t += 1.0 / 30.0
        }
        return frames
    }

    private struct FrameCell: View {
        let frame: Frame
        var body: some View {
            VStack(spacing: 0) {
                Text("\(Int(frame.at))s \(String(frame.phase.prefix(5)))").font(.system(size: 8)).lineLimit(1)
                Image(decorative: frame.image, scale: 2).resizable().interpolation(.none).frame(width: 44, height: 44)
                Text(frame.state).font(.system(size: 8)).lineLimit(1).minimumScaleFactor(0.6)
            }
            .frame(width: 56)
        }
    }

    @MainActor
    private static func strip(title: String, today: [Frame], grouped: [Frame]) -> NSImage? {
        let perRow = 28
        let view = VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 9))
            ForEach(0..<((today.count + perRow - 1) / perRow), id: \.self) { line in
                ForEach(0..<2, id: \.self) { mode in
                    let frames = mode == 0 ? today : grouped
                    HStack(spacing: 4) {
                        ForEach(Array(frames[(line * perRow)..<min(frames.count, (line + 1) * perRow)].enumerated()), id: \.offset) { _, frame in
                            FrameCell(frame: frame)
                        }
                    }
                }
            }
        }
        .foregroundColor(Color(red: 0.09, green: 0.1, blue: 0.12))
        .padding(4)
        .background(Color(red: 0.95, green: 0.95, blue: 0.94))
        let renderer = ImageRenderer(content: view)
        renderer.scale = 2
        return renderer.nsImage
    }

    /// Every look of every agent in both styles, into `folder`.
    @MainActor
    static func run(into folder: String) {
        let url = URL(fileURLWithPath: folder)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        PixelFace.register()
        RenderFlags.offscreen = true
        PixelFace.scale = 2
        guard let bridge = AppController.shared.bridge else { NSLog("render: no Core"); return }
        var written = 0
        for theme in ["pixel", "classic"] {
            guard let engine = try? CharacterEngine(theme: theme, directory: AppController.enginesDirectory()) else {
                NSLog("render: engine \(theme) did not load")
                continue
            }
            let pixel = theme == "pixel"
            let turns = pixel ? pixelTurns : classicTurns
            var trace = ""
            // One bot per agent, each at its own tempo as in the drawer.
            var indexes: [String: Int] = [:]
            var tempos: [String: Double] = [:]
            for (position, kind) in kinds.enumerated() {
                let tempo = (bridge.request("rotationStates", ["theme": theme, "id": kind, "kind": kind, "look": SampleLooks.look(kind, pixel ? .pixel : .classic).id])["tempo"] as? Double) ?? 1
                tempos[kind] = tempo
                indexes[kind] = engine.addBot(kind: kind, tempo: tempo, seed: 7 + position * 13, look: SampleLooks.look(kind, pixel ? .pixel : .classic).id)
            }
            for kind in kinds {
                guard let text = turns[kind], let index = indexes[kind] else { continue }
                let parsed = turn(text)
                let reply = bridge.request("looks", ["id": kind, "theme": theme])
                for look in LooksModel.from(reply)?.looks ?? [] {
                    engine.setLook(index, look.id)
                    let states = (bridge.request("rotationStates", ["theme": theme, "id": kind, "kind": kind, "look": look.id])["states"] as? [String]) ?? []
                    let cycles = engine.cycles(kind: kind, look: look.id, states: states, tempo: tempos[kind] ?? 1)
                    bridge.request("rotationSetup", ["theme": theme, "bots": [["id": kind, "kind": kind, "look": look.id, "cycles": cycles] as [String: Any]]])
                    let label = "\(theme) \(kind)/\(look.id)"
                    let today = play(engine: engine, index: index, count: kinds.count, theme: theme, id: kind, turn: parsed, grouped: false, label: label, trace: &trace, pixel: pixel)
                    let grouped = play(engine: engine, index: index, count: kinds.count, theme: theme, id: kind, turn: parsed, grouped: true, label: label, trace: &trace, pixel: pixel)
                    let title = "\(theme) · \(kind)/\(look.id) · a frame every 2 s · top row today, bottom row grouped · Mac drawer pipeline"
                    if let image = strip(title: title, today: today, grouped: grouped), let tiff = image.tiffRepresentation,
                       let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:]) {
                        try? png.write(to: url.appendingPathComponent("groups-\(theme)-\(kind)-\(look.id).png"))
                        written += 1
                    }
                }
            }
            try? trace.write(to: url.appendingPathComponent("groups-trace-\(theme).txt"), atomically: true, encoding: .utf8)
        }
        NSLog("render: wrote \(written) group strips to \(folder)")
    }
}
