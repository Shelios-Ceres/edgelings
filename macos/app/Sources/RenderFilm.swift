// Contact sheets of the drawer's moments (M1 reveal and hide, M6 lift, drop
// and refusal, M20 flip): the same tracks the drawer plays, sampled every
// third of a tick-ish step and drawn with the drawer's own painter. Each row
// is a frame of the film, left to right, three characters in a column.
import AppKit

enum RenderFilm {
    @MainActor
    static func sheet(_ name: String, pixel: Bool, dark: Bool) -> NSImage? {
        let kinds = ["claude", "codex", "antigravity"]
        let theme = pixel ? "pixel" : "classic"
        var images: [CGImage] = []
        for (position, kind) in kinds.enumerated() {
            guard let image = DrawerColumn.bot(kind: kind, pixel: pixel, index: position, scale: 2) else { return nil }
            images.append(image)
        }
        _ = theme
        // The slots as the drawer lays them out: 6 in, 12 down, 54 apart.
        let bounds = (0..<3).map { CGRect(x: 6, y: 12 + CGFloat($0) * 54, width: 44, height: 44) }
        let moment = DrawerMoment(start: 0, pixel: pixel, edge: 1)
        var duration = 0.0
        switch name {
        case "reveal": _ = DrawerMoves.reveal(moment, [0, 1, 2])
        case "hide": _ = DrawerMoves.hide(moment, [0, 1, 2], staggered: !pixel)
        case "lift-drop":
            _ = DrawerMoves.lift(moment, [0, 1, 2])
            duration = 0
        case "refuse":
            if pixel { _ = DrawerMoves.shake(moment, [0, 1, 2]) } else { _ = DrawerMoves.spring(moment, [0, 1, 2], pulled: 8) }
        default: _ = DrawerMoves.flip(moment, 0, reduced: false) {}
        }
        let drop = DrawerMoment(start: 0, pixel: pixel, edge: 1)
        if name == "lift-drop" { _ = DrawerMoves.drop(drop, [0, 1, 2], from: { _ in DrawerMoves.restAfterLift(pixel: pixel) }) }
        duration = moment.duration + (name == "lift-drop" ? drop.duration + 120 : 0)
        let step = pixel ? Motion.tickMs : 1000.0 / 30
        let count = min(24, max(2, Int((duration / step).rounded(.up)) + 1))
        // The refusal speaks: "Locked in place." pops beside the pressed character, so its cells are wide.
        let speaks = name == "refuse"
        let columns = speaks ? 5 : 12
        let rows = (count + columns - 1) / columns
        let cellW = speaks ? 250 : 64, cellH = 180
        var lockedBubble: (image: CGImage, size: CGSize)?
        if speaks {
            let palette = Palette(skin: pixel ? .pixel : .classic, dark: dark)
            let view = BubbleView(line: "Locked in place.\nRight-click to unlock.", kind: "plain", swatch: 0xD97757, palette: palette, rightEdge: true, tailY: 30)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 2
            if let image = renderer.cgImage { lockedBubble = (image, CGSize(width: CGFloat(image.width) / 2, height: CGFloat(image.height) / 2)) }
        }
        let scale = 2
        let width = columns * cellW * scale, height = rows * cellH * scale
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: width * 4, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setFillColor(dark ? CGColor(srgbRed: 0.11, green: 0.11, blue: 0.125, alpha: 1) : CGColor(srgbRed: 0.79, green: 0.8, blue: 0.83, alpha: 1))
        ctx.fill(CGRect(x: 0, y: 0, width: width, height: height))
        for frame in 0..<count {
            let ms = Double(frame) * step
            let column = frame % columns, row = frame / columns
            ctx.saveGState()
            // A flipped, point-based space for this frame's cell; the right edge of the cell is the screen edge.
            ctx.translateBy(x: CGFloat(column * cellW * scale), y: CGFloat((rows - row) * cellH * scale))
            ctx.scaleBy(x: CGFloat(scale), y: -CGFloat(scale))
            ctx.clip(to: CGRect(x: 0, y: 0, width: cellW, height: cellH))
            for index in 0..<3 {
                var pose: BotPose
                if name == "lift-drop" {
                    let liftEnd = moment.duration + 60
                    pose = ms <= liftEnd ? moment.pose(index, ms) : drop.pose(index, ms - liftEnd)
                } else {
                    pose = moment.pose(index, ms)
                }
                BotPainter.paint(ctx, image: images[index], bounds: bounds[index].offsetBy(dx: CGFloat(cellW - 64 + 8), dy: 0), pose: pose, pixel: pixel, edge: 1, dark: dark, scale: CGFloat(scale))
            }
            if let bubble = lockedBubble, frame >= 1 {
                // Beside the pressed character (the first), its pointer on the character's centre.
                let inset = BubbleView.inset(pixel)
                let x = CGFloat(cellW - 64 + 8) - 10 - bubble.size.width + inset
                let y = 12 + 22 - bubble.size.height / 2
                ctx.saveGState()
                ctx.translateBy(x: x, y: y + bubble.size.height)
                ctx.scaleBy(x: 1, y: -1)
                ctx.draw(bubble.image, in: CGRect(x: 0, y: 0, width: bubble.size.width, height: bubble.size.height))
                ctx.restoreGState()
            }
            ctx.restoreGState()
        }
        guard let out = ctx.makeImage() else { return nil }
        return NSImage(cgImage: out, size: NSSize(width: width / scale, height: height / scale))
    }
}

extension DrawerMoves {
    /// The pose a lifted bot rests in, for the drop that follows.
    static func restAfterLift(pixel: Bool) -> BotPose {
        var pose = BotPose.rest
        if pixel { pose.x = -4; pose.shadow = 1 } else { pose.scaleX = 1.07; pose.scaleY = 1.07; pose.shadow = 1; pose.shadowY = 3 }
        return pose
    }
}
