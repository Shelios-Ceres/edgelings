// `Edgelings --bench-loop [--scale 2] [--rounds 300] [--max-busy N] [--max-calm N]`:
// what the drawer's loop costs per frame, stage by stage, with seven characters
// at a Retina backing scale. It runs the real engines on JavaScriptCore and the
// real SvgCanvas into bitmaps the size the drawer uses, then turns the per-frame
// costs into "% of one core" at the frame rates the loop runs (busy: Pixel 30,
// Classic 60; calm: 30). Prints stage times; exits 1 when a bound is passed.
import Foundation
import AppKit
import CoreGraphics

enum BenchLoop {
    private static func milliseconds(_ block: () -> Void) -> Double {
        let start = DispatchTime.now().uptimeNanoseconds
        block()
        return Double(DispatchTime.now().uptimeNanoseconds - start) / 1_000_000
    }

    private static func value(_ name: String, _ arguments: [String], _ fallback: Double) -> Double {
        guard let at = arguments.firstIndex(of: name), at + 1 < arguments.count, let number = Double(arguments[at + 1]) else { return fallback }
        return number
    }

    static func run(arguments: [String]) -> Never {
        setvbuf(stdout, nil, _IONBF, 0)
        let scale = CGFloat(value("--scale", arguments, 2))
        let rounds = Int(value("--rounds", arguments, 300))
        let maxBusy = value("--max-busy", arguments, 1000)
        let maxCalm = value("--max-calm", arguments, 1000)
        var failed = false
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        let pixels = Int((Metrics.botSize * scale).rounded())
        let kinds = ["claude", "codex", "antigravity", "grok", "kimi", "deepseek", "claude"]
        // What the drawer shows while an agent's app is closed (the runner, and a Mac with its agents quit).
        let restStates = ["resting", "sleeping", "offline", "resting", "locked", "resting", "sleeping"]
        let busyStates = ["thinking", "writing", "reading", "running", "searching", "streaming", "web"]
        print("bench-loop: \(kinds.count) characters, backing scale \(scale), \(pixels) px bitmaps, \(rounds) rounds")
        let only = arguments.firstIndex(of: "--theme").flatMap { $0 + 1 < arguments.count ? arguments[$0 + 1] : nil }
        for theme in ["pixel", "classic"] where only == nil || only == theme {
            let pixel = theme == "pixel"
            for mode in ["busy", "calm", "rest"] {
                print("bench: \(theme) \(mode): loading the engine")
                let engine: CharacterEngine
                do {
                    engine = try CharacterEngine(theme: theme, directory: AppController.enginesDirectory())
                } catch {
                    print("FAIL \(theme): \(error)")
                    exit(1)
                }
                for (index, kind) in kinds.enumerated() {
                    let bot = engine.addBot(kind: kind, tempo: 1, seed: 7 + index * 13, look: "")
                    engine.setState(bot, mode == "busy" ? busyStates[index % busyStates.count] : mode == "rest" ? restStates[index % restStates.count] : "idle", phase: pixel ? 0 : -1)
                }
                print("bench: \(theme) \(mode): engine ready, \(kinds.count) bots added")
                let fps: Double = mode == "busy" ? (pixel ? 30 : 60) : 30
                let dts = [Double?](repeating: 1 / fps, count: kinds.count)
                let canvas = SvgCanvas()
                var last = [String?](repeating: nil, count: kinds.count)
                var jscSvg = 0.0, jscAdvance = 0.0, handover = 0.0, splitting = 0.0, early = 0.0, late = 0.0
                var jsc = 0.0, parse = 0.0, raster = 0.0, legacy = 0.0, blit = 0.0
                var redraws = 0
                let target = CGContext(data: nil, width: pixels * kinds.count, height: pixels, bitsPerComponent: 8, bytesPerRow: pixels * kinds.count * 4,
                                       space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
                var images = [CGImage?](repeating: nil, count: kinds.count)
                for round in 0..<(rounds + 30) {
                    var frames: [EngineFrame?] = []
                    let tickMs = milliseconds { frames = engine.tick(dts, size: Double(Metrics.botSize)) }
                    // The same work split: the SVG alone, and the call with the Swift handling apart. Not counted in the total.
                    let svgMs = milliseconds { engine.benchSvg(size: Double(Metrics.botSize)) }
                    let advanceMs = milliseconds { engine.benchAdvance(dts) }
                    let split = engine.benchTick(dts, size: Double(Metrics.botSize))
                    var roundChanged = false
                    var roundParse = 0.0, roundRaster = 0.0, roundLegacy = 0.0
                    for (index, produced) in frames.enumerated() {
                        guard let produced, produced.svg != last[index] else { continue }
                        last[index] = produced.svg
                        roundChanged = true
                        roundParse += milliseconds { _ = FastSvg.parse(produced.svg) }
                        if round % 10 == 0 {
                            roundLegacy += milliseconds {
                                // XMLParser keeps its delegate weakly: hold it for the parse.
                                let delegate = BenchDelegate()
                                let xml = XMLParser(data: Data(produced.svg.utf8))
                                xml.delegate = delegate
                                _ = xml.parse()
                                withExtendedLifetime(delegate) {}
                            }
                        }
                        roundRaster += milliseconds {
                            guard let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: pixels * 4,
                                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
                            ctx.translateBy(x: 0, y: CGFloat(pixels))
                            ctx.scaleBy(x: scale, y: -scale)
                            try? canvas.render(svg: produced.svg, into: ctx, antialias: !pixel)
                            images[index] = ctx.makeImage()
                        }
                    }
                    var roundBlit = 0.0
                    if roundChanged {
                        roundBlit = milliseconds {
                            target.clear(CGRect(x: 0, y: 0, width: pixels * kinds.count, height: pixels))
                            for (index, image) in images.enumerated() {
                                if let image { target.draw(image, in: CGRect(x: index * pixels, y: 0, width: pixels, height: pixels)) }
                            }
                        }
                    }
                    if round == 0 { print("bench: first round done, \(frames.compactMap { $0 }.count) frames") }
                    guard round >= 30 else { continue }
                    jsc += tickMs
                    jscSvg += svgMs
                    jscAdvance += advanceMs
                    handover += split.call
                    splitting += split.split
                    if round < 130 { early += tickMs } else if round >= rounds - 70 + 30 { late += tickMs }
                    parse += roundParse
                    raster += roundRaster
                    legacy += roundLegacy
                    blit += roundBlit
                    if roundChanged { redraws += 1 }
                }
                let perRound = { (total: Double) in total / Double(rounds) }
                let drawn = max(1, redraws)
                // Per second: `fps` rounds, each stepping every character once.
                let costPerSecond = fps * (perRound(jsc) + perRound(raster) + perRound(blit))
                let percent = costPerSecond / 10
                print(String(format: "bench %@ %@ @%.0f fps: jsc %.3f ms/round, svg parse %.3f, raster (parse included) %.3f, blit %.3f ms/round (%.0f%% of rounds change a frame); old XMLParser %.3f ms/round sampled; about %.1f%% of one core",
                             theme, mode, fps, perRound(jsc), perRound(parse), perRound(raster), perRound(blit), 100 * Double(drawn) / Double(rounds),
                             perRound(legacy * 10), percent))
                print(String(format: "bench %@ %@ split: svg alone %.3f ms/round, advance alone %.3f, tick call incl. string handover %.3f, Swift splitting %.3f; tick in rounds 30-130 %.3f, in the last 70 %.3f ms/round (JIT warm-up)",
                             theme, mode, perRound(jscSvg), perRound(jscAdvance), perRound(handover), perRound(splitting), early / 100, late / 70))
                if mode == "busy" && percent > maxBusy { print("FAIL \(theme) busy is over \(maxBusy)%"); failed = true }
                if mode != "busy" && percent > maxCalm { print("FAIL \(theme) calm is over \(maxCalm)%"); failed = true }
            }
        }
        exit(failed ? 1 : 0)
    }
}

private final class BenchDelegate: NSObject, XMLParserDelegate {}
