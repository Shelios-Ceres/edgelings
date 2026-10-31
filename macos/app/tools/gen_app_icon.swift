// Draws Edgelings' app icon: the Pixel robot head on a macOS rounded-square
// plate, at every size an .icns needs. The grids and colours are the Pixel
// design chat's (robot-head-grids.json, copied from
// windows/notes/pixel-icon-20261014/v2/grids.json); no mouth at any size and the
// antenna one step right of centre are in the grids themselves.
//
//   swiftc -O gen_app_icon.swift -o gen-icon
//   gen-icon <robot-head-grids.json> <out.iconset> [<preview-folder>]
//
// The plate is 824/1024 of the canvas, centred, corner radius 185/1024, in the
// Pixel skin's light Paper (#FFFDF7), with the standard soft drop shadow and no
// gloss. Only the plate's edge and the shadow are antialiased: the head is
// drawn in whole device pixels. Head grid and cell size per entry are the
// design chat's (about 70 % of the plate's width).
import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

struct Spec: Decodable {
    let colors: [String: String]
    let grids: [String: [String]]
}

let arguments = CommandLine.arguments
guard arguments.count >= 3 else {
    print("usage: gen-icon <robot-head-grids.json> <out.iconset> [<preview-folder>]")
    exit(2)
}
guard let data = FileManager.default.contents(atPath: arguments[1]), let spec = try? JSONDecoder().decode(Spec.self, from: data) else {
    print("cannot read \(arguments[1])")
    exit(1)
}
let iconset = arguments[2]
let previews = arguments.count > 3 ? arguments[3] : nil
try? FileManager.default.createDirectory(atPath: iconset, withIntermediateDirectories: true)
if let previews { try? FileManager.default.createDirectory(atPath: previews, withIntermediateDirectories: true) }

/// pixel size, head grid, cell size in pixels
let entries: [(px: Int, grid: String, cell: Int)] = [
    (16, "8", 1), (32, "10", 2), (64, "12", 3), (128, "12", 6), (256, "16", 9), (512, "16", 18), (1024, "16", 36),
]

let space = CGColorSpace(name: CGColorSpace.sRGB)!

func color(_ hex: String) -> CGColor {
    let value = UInt32(hex.dropFirst(), radix: 16) ?? 0
    return CGColor(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255, blue: CGFloat(value & 0xFF) / 255, alpha: 1)
}

func render(_ px: Int, grid: String, cell: Int) -> CGImage {
    let ctx = CGContext(data: nil, width: px, height: px, bitsPerComponent: 8, bytesPerRow: px * 4, space: space,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    let side = CGFloat(px)
    let plate = side * 824 / 1024
    let origin = (side - plate) / 2
    let radius = side * 185 / 1024
    let rect = CGRect(x: origin, y: origin, width: plate, height: plate)
    let path = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)
    // The plate with its soft drop shadow, no gloss.
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -side * 10 / 1024), blur: side * 28 / 1024,
                  color: CGColor(srgbRed: 0, green: 0, blue: 0, alpha: 0.32))
    ctx.setFillColor(color("#FFFDF7"))
    ctx.addPath(path)
    ctx.fillPath()
    ctx.restoreGState()
    // The head, on whole device pixels.
    let rows = spec.grids[grid] ?? []
    let width = (rows.map { $0.count }.max() ?? 0) * cell
    let height = rows.count * cell
    let left = (px - width) / 2
    let top = (px - height) / 2
    ctx.setShouldAntialias(false)
    for (y, row) in rows.enumerated() {
        for (x, key) in row.enumerated() where key != "." {
            guard let hex = spec.colors[String(key)] else { continue }
            ctx.setFillColor(color(hex))
            ctx.fill(CGRect(x: left + x * cell, y: px - (top + (y + 1) * cell), width: cell, height: cell))
        }
    }
    return ctx.makeImage()!
}

func write(_ image: CGImage, to path: String) {
    let url = URL(fileURLWithPath: path) as CFURL
    guard let destination = CGImageDestinationCreateWithURL(url, UTType.png.identifier as CFString, 1, nil) else { return }
    CGImageDestinationAddImage(destination, image, nil)
    CGImageDestinationFinalize(destination)
}

var images: [Int: CGImage] = [:]
for entry in entries { images[entry.px] = render(entry.px, grid: entry.grid, cell: entry.cell) }

// The names iconutil wants; the same pixels serve both scales of a size.
let names: [(String, Int)] = [
    ("icon_16x16.png", 16), ("icon_16x16@2x.png", 32), ("icon_32x32.png", 32), ("icon_32x32@2x.png", 64),
    ("icon_128x128.png", 128), ("icon_128x128@2x.png", 256), ("icon_256x256.png", 256), ("icon_256x256@2x.png", 512),
    ("icon_512x512.png", 512), ("icon_512x512@2x.png", 1024),
]
for (name, px) in names { if let image = images[px] { write(image, to: iconset + "/" + name) } }

if let previews {
    if let big = images[1024] { write(big, to: previews + "/app-icon-1024.png") }
    // Every size at 1x, side by side on a neutral ground.
    let sizes = [16, 32, 64, 128, 256, 512]
    let gap = 24
    let stripWidth = sizes.reduce(gap) { $0 + $1 + gap }
    let stripHeight = 512 + 2 * gap
    for (tag, ground) in [("light", CGColor(srgbRed: 0.93, green: 0.93, blue: 0.94, alpha: 1)), ("dark", CGColor(srgbRed: 0.12, green: 0.12, blue: 0.14, alpha: 1))] {
        let ctx = CGContext(data: nil, width: stripWidth, height: stripHeight, bitsPerComponent: 8, bytesPerRow: stripWidth * 4, space: space,
                            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(ground)
        ctx.fill(CGRect(x: 0, y: 0, width: stripWidth, height: stripHeight))
        var x = gap
        for size in sizes {
            if let image = images[size] { ctx.draw(image, in: CGRect(x: x, y: stripHeight - gap - size, width: size, height: size)) }
            x += size + gap
        }
        if let strip = ctx.makeImage() { write(strip, to: previews + "/app-icon-strip-\(tag).png") }
    }
}
print("wrote \(names.count) iconset entries")
