// Ark Pixel drawn the way a pixel face has to be: with no antialiasing, no
// font smoothing and no subpixel positioning, each font pixel a whole number
// of device pixels (PixelFace.size), the baseline on a device pixel. SwiftUI's
// Text offers no switch for any of that, so the pixel skin sets its lines
// here, through Core Text.
import SwiftUI
import CoreText

/// One run of a line: its text and ink.
struct PixelRun {
    let text: String
    let color: Color
}

enum PixelTypeset {
    static func font(_ size: CGFloat) -> CTFont {
        PixelFace.register()
        return CTFontCreateWithName((PixelFace.family ?? "Menlo") as CFString, size, nil)
    }

    /// The line box: whole cells, so the baseline lands on a whole pixel (BodyLine 20 at the body size).
    static func lineHeight(_ size: CGFloat) -> CGFloat {
        let raw = size * 4 / 3
        return (raw / 2).rounded(.up) * 2
    }

    static func attributed(_ runs: [PixelRun], size: CGFloat) -> NSAttributedString {
        let font = font(size)
        let result = NSMutableAttributedString()
        for run in runs {
            let color = NSColor(run.color).usingColorSpace(.sRGB)?.cgColor ?? CGColor(gray: 0, alpha: 1)
            result.append(NSAttributedString(string: run.text, attributes: [
                NSAttributedString.Key(rawValue: kCTFontAttributeName as String): font,
                NSAttributedString.Key(rawValue: kCTForegroundColorAttributeName as String): color,
            ]))
        }
        return result
    }

    /// The lines the text breaks into at `width` (nil: one line), at most `limit`.
    static func lines(_ text: NSAttributedString, width: CGFloat?, limit: Int?) -> [CTLine] {
        let typesetter = CTTypesetterCreateWithAttributedString(text)
        var result: [CTLine] = []
        var start = 0
        let length = text.length
        while start < length {
            if let limit, result.count >= limit { break }
            let count: Int
            if let width, width.isFinite { count = max(1, CTTypesetterSuggestLineBreak(typesetter, start, Double(width))) } else { count = length - start }
            result.append(CTTypesetterCreateLine(typesetter, CFRange(location: start, length: count)))
            start += count
        }
        return result
    }

    static func width(of line: CTLine) -> CGFloat {
        var ascent: CGFloat = 0, descent: CGFloat = 0, leading: CGFloat = 0
        let w = CTLineGetTypographicBounds(line, &ascent, &descent, &leading)
        // A trailing space is not ink.
        return CGFloat(w) - CGFloat(CTLineGetTrailingWhitespaceWidth(line))
    }
}

/// Lays a pixel line out: the width of its longest line (capped by the proposal), a whole-cell line box per line.
struct PixelTextLayout: Layout {
    let runs: [PixelRun]
    let dip: CGFloat
    let limit: Int?

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let size = PixelFace.size(dip)
        let text = PixelTypeset.attributed(runs, size: size)
        let lines = PixelTypeset.lines(text, width: proposal.width, limit: limit)
        let widest = lines.map { PixelTypeset.width(of: $0) }.max() ?? 0
        let scale = PixelFace.scale
        return CGSize(width: (widest * scale).rounded(.up) / scale, height: CGFloat(max(lines.count, 1)) * PixelTypeset.lineHeight(size))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for subview in subviews { subview.place(at: bounds.origin, proposal: ProposedViewSize(bounds.size)) }
    }
}

struct PixelText: View {
    let runs: [PixelRun]
    var dip: CGFloat = 13
    var limit: Int?

    init(_ text: String, dip: CGFloat = 13, color: Color, lines: Int? = nil) {
        runs = [PixelRun(text: text, color: color)]
        self.dip = dip
        limit = lines
    }

    init(runs: [PixelRun], dip: CGFloat = 13, lines: Int? = nil) {
        self.runs = runs
        self.dip = dip
        limit = lines
    }

    var body: some View {
        PixelTextLayout(runs: runs, dip: dip, limit: limit) {
            Canvas { context, size in
                let fontSize = PixelFace.size(dip)
                let text = PixelTypeset.attributed(runs, size: fontSize)
                let lines = PixelTypeset.lines(text, width: size.width + 0.5, limit: limit)
                let height = PixelTypeset.lineHeight(fontSize)
                let font = PixelTypeset.font(fontSize)
                let ascent = CTFontGetAscent(font), descent = CTFontGetDescent(font)
                let scale = PixelFace.scale
                context.withCGContext { cg in
                    cg.saveGState()
                    cg.setShouldAntialias(false)
                    cg.setShouldSmoothFonts(false)
                    cg.setAllowsFontSubpixelPositioning(false)
                    cg.setShouldSubpixelPositionFonts(false)
                    cg.setShouldSubpixelQuantizeFonts(false)
                    cg.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
                    for (index, line) in lines.enumerated() {
                        let top = CGFloat(index) * height
                        let baseline = ((top + (height - (ascent + descent)) / 2 + ascent) * scale).rounded() / scale
                        cg.textPosition = CGPoint(x: 0, y: baseline)
                        CTLineDraw(line, cg)
                    }
                    cg.restoreGState()
                }
            }
        }
    }
}

/// A line in the skin's face: Pixel through Core Text with no antialiasing,
/// Classic as system text.
struct SkinText: View {
    let pixel: Bool
    let text: String
    var pixelSize: CGFloat = 13
    var size: CGFloat = 13
    var weight: Font.Weight = .regular
    let color: Color
    var lines: Int?

    var body: some View {
        if pixel {
            PixelText(text, dip: pixelSize, color: color, lines: lines)
        } else {
            Text(text).font(.system(size: size, weight: weight)).foregroundColor(color).lineLimit(lines)
        }
    }
}

/// A phrase with each of `numbers`, found in order, lifted to the strong ink
/// (and, in Classic, semibold).
struct EmphasisedText: View {
    let pixel: Bool
    let phrase: String
    let numbers: [String]
    let strong: Color
    let base: Color
    var pixelSize: CGFloat = 13
    var size: CGFloat = 12

    var body: some View {
        if pixel {
            PixelText(runs: EmphasisedText.runs(phrase, numbers, strong: strong, base: base), dip: pixelSize)
        } else {
            emphasised(phrase, numbers: numbers, strong: strong, bold: true).font(.system(size: size)).foregroundColor(base)
        }
    }

    static func runs(_ phrase: String, _ numbers: [String], strong: Color, base: Color) -> [PixelRun] {
        var result: [PixelRun] = []
        var rest = Substring(phrase)
        for number in numbers where !number.isEmpty {
            guard let range = rest.range(of: number) else { continue }
            if range.lowerBound > rest.startIndex { result.append(PixelRun(text: String(rest[rest.startIndex..<range.lowerBound]), color: base)) }
            result.append(PixelRun(text: number, color: strong))
            rest = rest[range.upperBound...]
        }
        if !rest.isEmpty { result.append(PixelRun(text: String(rest), color: base)) }
        return result
    }
}
