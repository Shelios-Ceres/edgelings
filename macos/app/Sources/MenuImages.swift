// Images for the menus and the menu bar: the Pixel kit's icons and faces, the
// Classic faces, and the menu-bar head in both styles.
import AppKit

enum MenuImages {
    /// A pixel grid as an image, one cell = `cell` points (drawn at 2x).
    static func grid(_ rows: [String], colors: [Character: UInt32], cell: CGFloat, ink: NSColor? = nil, clear: Set<Character> = []) -> NSImage {
        let width = CGFloat(rows.map { $0.count }.max() ?? 10) * cell
        let height = CGFloat(rows.count) * cell
        let image = NSImage(size: NSSize(width: width, height: height), flipped: true) { _ in
            for (y, row) in rows.enumerated() {
                for (x, key) in row.enumerated() where key != "." && key != " " && !clear.contains(key) {
                    let color: NSColor
                    if let ink { color = ink } else if let value = colors[key] {
                        color = NSColor(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
                                        blue: CGFloat(value & 0xFF) / 255, alpha: 1)
                    } else { continue }
                    color.setFill()
                    NSRect(x: CGFloat(x) * cell, y: CGFloat(y) * cell, width: cell, height: cell).fill()
                }
            }
            return true
        }
        return image
    }

    /// A kit icon at 20 pt in the menu's text colour.
    static func pixelIcon(_ name: String, dark: Bool) -> NSImage? {
        guard let rows = PixelArt.icons[name] else { return nil }
        let image = grid(rows, colors: [:], cell: 2, ink: .black)
        image.isTemplate = true
        return image
    }

    /// An agent's face for a look: Pixel's 10 x 10 art at 20 pt, Classic's
    /// desk companion at its resting frame, 20 pt.
    static func face(agent: String, look: String, swatch: UInt32, pixel: Bool) -> NSImage? {
        if pixel {
            let art = PixelArt.lookFaces[agent + "/" + look] ?? PixelArt.faces[agent] ?? PixelArt.logo
            return grid(art.rows, colors: art.colors, cell: 2)
        }
        guard let frame = BotPreview.image(theme: "classic", kind: agent, look: look) else { return nil }
        let image = NSImage(cgImage: frame, size: NSSize(width: 44, height: 44))
        image.size = NSSize(width: 20, height: 20)
        return image
    }
}

/// The menu-bar icon: a template image the system tints for light and dark
/// bars. Pixel: the app's robot head on the 2-pt cell grid. Classic: a
/// rounded head with two pill eyes, 16 pt in the 18-pt slot, a 1.5-pt stroke.
enum MenuBarIcon {
    static func image(pixel: Bool) -> NSImage {
        let image = pixel ? pixelHead() : classicHead()
        image.isTemplate = true
        return image
    }

    private static func pixelHead() -> NSImage {
        // The logo grid: outline x, face v (left clear), eyes e, antenna o.
        let art = PixelArt.logo
        let image = MenuImages.grid(art.rows, colors: [:], cell: 2, ink: .black, clear: ["v"])
        image.size = NSSize(width: 20, height: 20)
        return image
    }

    /// A filled head, about 16 x 15 on whole points, with the two pill eyes knocked out 42 % of the way down.
    private static func classicHead() -> NSImage {
        let size = NSSize(width: 18, height: 18)
        return NSImage(size: size, flipped: false) { _ in
            NSColor.black.setFill()
            NSBezierPath(roundedRect: NSRect(x: 1, y: 2, width: 16, height: 15), xRadius: 5, yRadius: 5).fill()
            NSGraphicsContext.current?.compositingOperation = .clear
            for x in [5, 11] as [CGFloat] {
                NSBezierPath(roundedRect: NSRect(x: x, y: 8, width: 2, height: 5), xRadius: 1, yRadius: 1).fill()
            }
            return true
        }
    }
}
