// The Mac port's drawing check. For every frame in macos/golden/frames (made on
// Windows by V8 and Win2D) this
//   1. replays the bot in JavaScriptCore and compares the SVG text, and
//   2. draws the Windows SVG and the JavaScriptCore SVG with Core Graphics and
//      compares both to the Windows PNG, pixel by pixel.
// usage: golden <frames-dir> <engines-dir> <out-dir> [--strict]
import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

let arguments = CommandLine.arguments
guard arguments.count >= 4 else {
    print("usage: golden <frames-dir> <engines-dir> <out-dir> [--strict]")
    exit(2)
}
let framesDirectory = arguments[1], enginesDirectory = arguments[2], outDirectory = arguments[3]
let strict = arguments.contains("--strict")
let scale: CGFloat = 2          // 44 DIP at 192 dpi, as the Windows frames were made
let size = 44
let pixels = size * 2
try? FileManager.default.createDirectory(atPath: outDirectory, withIntermediateDirectories: true)

// Per-channel difference (0-255, premultiplied RGBA) above which a pixel counts as different.
let channelTolerance = 32
// Share of pixels allowed to differ: Pixel draws unantialiased rectangles on a grid, Classic is antialiased vector art.
let allowedShare: [String: Double] = ["pixel": 0.002, "classic": 0.03]

func renderPNG(svg: String, antialias: Bool) throws -> [UInt8] {
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    guard let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: pixels * 4,
                              space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        throw EngineError.script("no bitmap context")
    }
    ctx.clear(CGRect(x: 0, y: 0, width: pixels, height: pixels))
    ctx.translateBy(x: 0, y: CGFloat(pixels))
    ctx.scaleBy(x: scale, y: -scale)
    try SvgCanvas().render(svg: svg, into: ctx, antialias: antialias)
    return bytes(of: ctx)
}

func bytes(of ctx: CGContext) -> [UInt8] {
    let count = ctx.bytesPerRow * ctx.height
    return Array(UnsafeBufferPointer(start: ctx.data!.assumingMemoryBound(to: UInt8.self), count: count))
}

func decode(path: String) throws -> [UInt8] {
    guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
        throw EngineError.script("cannot read \(path)")
    }
    let space = CGColorSpace(name: CGColorSpace.sRGB)!
    guard let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: pixels * 4,
                              space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        throw EngineError.script("no bitmap context")
    }
    ctx.clear(CGRect(x: 0, y: 0, width: pixels, height: pixels))
    ctx.draw(image, in: CGRect(x: 0, y: 0, width: pixels, height: pixels))
    return bytes(of: ctx)
}

func writePNG(_ data: [UInt8], to path: String) {
    var copy = data
    copy.withUnsafeMutableBytes { raw in
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: raw.baseAddress, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: pixels * 4,
                                  space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let image = ctx.makeImage(),
              let destination = CGImageDestinationCreateWithURL(URL(fileURLWithPath: path) as CFURL, UTType.png.identifier as CFString, 1, nil)
        else { return }
        CGImageDestinationAddImage(destination, image, nil)
        CGImageDestinationFinalize(destination)
    }
}

struct Difference {
    var maxChannel = 0
    var mean = 0.0
    var share = 0.0     // pixels with any channel off by more than the tolerance
}

func compare(_ a: [UInt8], _ b: [UInt8]) -> Difference {
    var result = Difference()
    var total = 0, over = 0
    for i in stride(from: 0, to: min(a.count, b.count), by: 4) {
        var worst = 0
        for c in 0..<4 {
            let d = abs(Int(a[i + c]) - Int(b[i + c]))
            worst = max(worst, d)
            total += d
        }
        if worst > channelTolerance { over += 1 }
        result.maxChannel = max(result.maxChannel, worst)
    }
    let pixelCount = Double(min(a.count, b.count) / 4)
    result.mean = Double(total) / (pixelCount * 4)
    result.share = Double(over) / pixelCount
    return result
}

func diffImage(_ a: [UInt8], _ b: [UInt8]) -> [UInt8] {
    var out = [UInt8](repeating: 0, count: a.count)
    for i in stride(from: 0, to: a.count, by: 4) {
        var worst = 0
        for c in 0..<4 { worst = max(worst, abs(Int(a[i + c]) - Int(b[i + c]))) }
        let level = UInt8(min(255, worst * 4))
        out[i] = level
        out[i + 3] = 255
    }
    return out
}

struct Frame {
    let name: String, theme: String, kind: String, look: String, state: String, tempo: Double, seed: Int
}

let table = try String(contentsOfFile: framesDirectory + "/frames.tsv", encoding: .utf8)
var frames: [Frame] = []
for line in table.split(separator: "\n").dropFirst() {
    let f = line.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
    guard f.count >= 8 else { continue }
    frames.append(Frame(name: f[0], theme: f[1], kind: f[2], look: f[3], state: f[4], tempo: Double(f[6]) ?? 1, seed: Int(f[7]) ?? 7))
}

var engines: [String: JSCEngine] = [:]
var report: [String] = []
var svgDifferent = 0, drawFailed = 0, engineFailed = 0
var worst: [(String, Double)] = []
var totals: [String: (frames: Int, bad: Int, mean: Double, maxChannel: Int)] = [:]

for frame in frames {
    let antialias = frame.theme != "pixel"
    guard let windowsSvg = try? String(contentsOfFile: framesDirectory + "/" + frame.name + ".svg", encoding: .utf8),
          let reference = try? decode(path: framesDirectory + "/" + frame.name + ".png") else {
        report.append("\(frame.name): fixture missing")
        engineFailed += 1
        continue
    }
    // 1. The engine in JavaScriptCore, replayed in the order the Windows run added its bots.
    var jscSvg: String?
    do {
        let engine: JSCEngine
        if let known = engines[frame.theme] { engine = known } else {
            engine = try JSCEngine(theme: frame.theme, directory: enginesDirectory)
            engines[frame.theme] = engine
        }
        let bot = try engine.add(kind: frame.kind, seed: frame.seed, tempo: frame.tempo, look: frame.look)
        try engine.setState(bot, frame.state, phase: 0)
        var svg = ""
        for _ in 0..<3 { svg = try engine.tick(bot: bot, dt: 0.4, size: Double(size)) }
        jscSvg = svg
    } catch {
        report.append("\(frame.name): engine failed: \(error)")
        engineFailed += 1
    }
    let sameSvg = jscSvg == windowsSvg
    if jscSvg != nil && !sameSvg { svgDifferent += 1 }
    // 2. Drawing.
    do {
        let drawn = try renderPNG(svg: windowsSvg, antialias: antialias)
        let difference = compare(drawn, reference)
        var key = totals[frame.theme] ?? (0, 0, 0, 0)
        key.frames += 1
        key.mean += difference.mean
        key.maxChannel = max(key.maxChannel, difference.maxChannel)
        let allowed = allowedShare[frame.theme] ?? 0.01
        let bad = difference.share > allowed
        if bad { key.bad += 1 }
        totals[frame.theme] = key
        worst.append((frame.name, difference.share))
        var line = String(format: "%@: svg %@; CG vs Win2D max %d mean %.2f over %.2f%%%@", frame.name, sameSvg ? "same" : "differs",
                          difference.maxChannel, difference.mean, difference.share * 100, bad ? " OVER" : "")
        if let jscSvg, !sameSvg, let jscDrawn = try? renderPNG(svg: jscSvg, antialias: antialias) {
            let engineDifference = compare(jscDrawn, reference)
            line += String(format: "; JSC SVG drawn: max %d over %.2f%%", engineDifference.maxChannel, engineDifference.share * 100)
        }
        report.append(line)
        if bad {
            writePNG(drawn, to: outDirectory + "/" + frame.name + "-cg.png")
            writePNG(diffImage(drawn, reference), to: outDirectory + "/" + frame.name + "-diff.png")
        }
    } catch {
        report.append("\(frame.name): drawing failed: \(error)")
        drawFailed += 1
    }
}

try report.joined(separator: "\n").write(toFile: outDirectory + "/golden-report.txt", atomically: true, encoding: .utf8)
print("frames: \(frames.count); SVG text differs from V8's in \(svgDifferent); engine failures \(engineFailed); drawing failures \(drawFailed)")
for theme in ["pixel", "classic"] {
    if let t = totals[theme] {
        print(String(format: "%@: %d frames, %d over the tolerance (more than %.1f%% of pixels off by over %d), mean difference %.3f, worst channel difference %d",
                     theme, t.frames, t.bad, (allowedShare[theme] ?? 0) * 100, channelTolerance, t.mean / Double(max(1, t.frames)), t.maxChannel))
    }
}
print("worst frames:")
for (name, share) in worst.sorted(by: { $0.1 > $1.1 }).prefix(8) { print(String(format: "  %@ %.2f%%", name, share * 100)) }
let over = totals.values.reduce(0) { $0 + $1.bad }
if strict && (over > 0 || engineFailed > 0 || drawFailed > 0 || svgDifferent > 0) { exit(1) }
