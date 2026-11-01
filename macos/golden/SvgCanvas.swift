// The character engines' SVG subset on Core Graphics: svg, defs, g, path
// (M L H V C A Z, absolute), circle, use, clipPath, mask (as a clip of its
// shapes); transform, opacity, fill, fill-rule, stroke, stroke-width,
// stroke-dasharray, stroke-linecap / linejoin. A port of SvgCanvas.cs, which
// does the same with Win2D; the golden check compares the two.
import Foundation
import CoreGraphics

final class SvgNode {
    let name: String
    let attrs: [String: String]
    var children: [SvgNode] = []
    init(name: String, attrs: [String: String]) {
        self.name = name
        self.attrs = attrs
    }
    func attr(_ key: String) -> String? { attrs[key] }
}

/// A scanner for the engines' SVG: elements with quoted attributes, no text,
/// comments, entities or processing instructions. It is several times faster
/// than XMLParser, which the drawer's loop would otherwise run for every frame
/// of every character. Anything outside that subset answers nil, and the
/// caller falls back to XMLParser, so an unusual frame is still drawn.
enum FastSvg {
    static func parse(_ svg: String) -> SvgNode? {
        var copy = svg
        return copy.withUTF8 { parse($0) }
    }

    private static func parse(_ b: UnsafeBufferPointer<UInt8>) -> SvgNode? {
        let n = b.count
        var i = 0
        var root: SvgNode?
        var stack: [SvgNode] = []
        func isSpace(_ c: UInt8) -> Bool { c == 0x20 || c == 0x0A || c == 0x09 || c == 0x0D }
        func text(_ from: Int, _ to: Int) -> String { String(decoding: UnsafeBufferPointer(rebasing: b[from..<to]), as: UTF8.self) }
        while i < n {
            if b[i] != 0x3C { // '<'
                if !isSpace(b[i]) { return nil }
                i += 1
                continue
            }
            i += 1
            guard i < n else { return nil }
            if b[i] == 0x2F { // end tag
                while i < n, b[i] != 0x3E { i += 1 }
                guard i < n, !stack.isEmpty else { return nil }
                stack.removeLast()
                i += 1
                continue
            }
            if b[i] == 0x21 || b[i] == 0x3F { return nil }
            let nameStart = i
            while i < n, !isSpace(b[i]), b[i] != 0x2F, b[i] != 0x3E { i += 1 }
            guard i < n, i > nameStart else { return nil }
            let name = text(nameStart, i)
            var attrs: [String: String] = [:]
            var selfClosing = false
            attributes: while true {
                while i < n, isSpace(b[i]) { i += 1 }
                guard i < n else { return nil }
                if b[i] == 0x3E { i += 1; break attributes }
                if b[i] == 0x2F {
                    guard i + 1 < n, b[i + 1] == 0x3E else { return nil }
                    selfClosing = true
                    i += 2
                    break attributes
                }
                let keyStart = i
                while i < n, b[i] != 0x3D, !isSpace(b[i]), b[i] != 0x3E, b[i] != 0x2F { i += 1 }
                guard i < n, i > keyStart else { return nil }
                let key = text(keyStart, i)
                while i < n, isSpace(b[i]) { i += 1 }
                guard i < n, b[i] == 0x3D else { return nil }
                i += 1
                while i < n, isSpace(b[i]) { i += 1 }
                guard i < n, b[i] == 0x22 || b[i] == 0x27 else { return nil }
                let quote = b[i]
                i += 1
                let valueStart = i
                while i < n, b[i] != quote {
                    if b[i] == 0x26 || b[i] == 0x3C { return nil } // an entity, or a stray <
                    i += 1
                }
                guard i < n else { return nil }
                attrs[key] = text(valueStart, i)
                i += 1
            }
            let node = SvgNode(name: name, attrs: attrs)
            if let top = stack.last { top.children.append(node) } else if root == nil { root = node } else { return nil }
            if !selfClosing { stack.append(node) }
        }
        return stack.isEmpty ? root : nil
    }
}

private final class SvgParser: NSObject, XMLParserDelegate {
    var root: SvgNode?
    private var stack: [SvgNode] = []
    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?, attributes attributeDict: [String: String] = [:]) {
        let node = SvgNode(name: elementName, attrs: attributeDict)
        if let top = stack.last { top.children.append(node) } else { root = node }
        stack.append(node)
    }
    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName qName: String?) {
        stack.removeLast()
    }
}

struct Paint {
    var fill = "#000"
    var fillRule = "nonzero"
    var stroke: String?
    var strokeWidth: CGFloat = 1
    var dash: String?
    var cap: String?
    var join: String?

    func with(_ e: SvgNode) -> Paint {
        var p = self
        if let v = e.attr("fill") { p.fill = v }
        if let v = e.attr("fill-rule") { p.fillRule = v }
        if let v = e.attr("stroke") { p.stroke = v }
        if let v = e.attr("stroke-width"), let n = Double(v) { p.strokeWidth = CGFloat(n) }
        if let v = e.attr("stroke-dasharray") { p.dash = v }
        if let v = e.attr("stroke-linecap") { p.cap = v }
        if let v = e.attr("stroke-linejoin") { p.join = v }
        return p
    }
}

final class SvgCanvas {
    private var byId: [String: SvgNode] = [:]
    private var clips: [String: CGPath] = [:]
    /// Parsed path data by its text: a frame's shapes mostly repeat from the
    /// last (SvgCanvas.cs keeps the same cache); emptied past a bound.
    private var paths: [String: CGPath] = [:]
    private var colors: [String: CGColor] = [:]
    private static let space = CGColorSpace(name: CGColorSpace.sRGB)!

    /// Draws svg into ctx, whose current transform maps one SVG unit to the
    /// target's unit (the caller flips and scales it). antialias false is
    /// the Pixel style's aliased drawing.
    func render(svg: String, into ctx: CGContext, antialias: Bool) throws {
        let root: SvgNode
        if let fast = FastSvg.parse(svg) {
            root = fast
        } else {
            let parser = SvgParser()
            let xml = XMLParser(data: Data(svg.utf8))
            xml.delegate = parser
            guard xml.parse(), let parsed = parser.root else {
                throw NSError(domain: "svg", code: 1, userInfo: [NSLocalizedDescriptionKey: "unparsable SVG"])
            }
            root = parsed
        }
        byId = [:]
        clips = [:]
        index(root)
        ctx.setShouldAntialias(antialias)
        ctx.setAllowsAntialiasing(antialias)
        for child in root.children { draw(child, ctx, Paint()) }
    }

    private func index(_ node: SvgNode) {
        for child in node.children {
            if let id = child.attr("id") { byId[id] = child }
            index(child)
        }
    }

    private func draw(_ e: SvgNode, _ ctx: CGContext, _ inherited: Paint) {
        if e.name == "defs" || e.name == "clipPath" || e.name == "mask" { return }
        let paint = inherited.with(e)
        ctx.saveGState()
        if let transform = e.attr("transform") { apply(transform, to: ctx) }
        let opacity = e.attr("opacity").flatMap { Double($0) }.map { CGFloat($0) } ?? 1
        for attribute in ["clip-path", "mask"] {
            if let url = e.attr(attribute), let clipPath = clip(urlId(url)) {
                ctx.addPath(clipPath)
                ctx.clip(using: .winding)
            }
        }
        let layered = opacity < 1
        if layered {
            ctx.setAlpha(opacity)
            ctx.beginTransparencyLayer(auxiliaryInfo: nil)
        }
        switch e.name {
        case "g":
            for child in e.children { draw(child, ctx, paint) }
        case "use":
            if let target = reference(e) { draw(target, ctx, paint) }
        case "path", "circle":
            drawShape(ctx, shape(e), paint)
        default:
            break
        }
        if layered { ctx.endTransparencyLayer() }
        ctx.restoreGState()
    }

    private func drawShape(_ ctx: CGContext, _ path: CGPath, _ paint: Paint) {
        if paint.fill != "none" {
            ctx.addPath(path)
            ctx.setFillColor(color(paint.fill))
            ctx.fillPath(using: paint.fillRule == "evenodd" ? .evenOdd : .winding)
        }
        if let stroke = paint.stroke, stroke != "none", paint.strokeWidth > 0 {
            ctx.addPath(path)
            ctx.setStrokeColor(color(stroke))
            ctx.setLineWidth(paint.strokeWidth)
            ctx.setLineCap(paint.cap == "round" ? .round : .butt)
            ctx.setLineJoin(paint.join == "round" ? .round : .miter)
            if let dash = paint.dash, dash != "none" {
                ctx.setLineDash(phase: 0, lengths: numbers(dash))
            } else {
                ctx.setLineDash(phase: 0, lengths: [])
            }
            ctx.strokePath()
        }
    }

    private func shape(_ e: SvgNode) -> CGPath {
        if e.name == "circle" {
            let cx = number(e.attr("cx") ?? "0"), cy = number(e.attr("cy") ?? "0"), r = number(e.attr("r") ?? "0")
            return CGPath(ellipseIn: CGRect(x: cx - r, y: cy - r, width: 2 * r, height: 2 * r), transform: nil)
        }
        let data = e.attr("d") ?? ""
        if let known = paths[data] { return known }
        let parsed = SvgPath.parse(data)
        if paths.count >= 2048 { paths.removeAll(keepingCapacity: true) }
        paths[data] = parsed
        return parsed
    }

    /// A clipPath's or mask's shapes as one path, in the user space of the element that uses it.
    private func clip(_ id: String) -> CGPath? {
        if let cached = clips[id] { return cached }
        guard let element = byId[id] else { return nil }
        let union = CGMutablePath()
        for child in element.children { collect(child, CGAffineTransform.identity, union) }
        clips[id] = union
        return union
    }

    private func collect(_ e: SvgNode, _ parent: CGAffineTransform, _ into: CGMutablePath) {
        var matrix = parent
        if let transform = e.attr("transform") {
            // As apply() does: each step is applied before the ones already there.
            for step in transforms(transform) { matrix = step.concatenating(matrix) }
        }
        switch e.name {
        case "g":
            for child in e.children { collect(child, matrix, into) }
        case "use":
            if let target = reference(e) { collect(target, matrix, into) }
        case "path", "circle":
            into.addPath(shape(e), transform: matrix)
        default:
            break
        }
    }

    private func reference(_ use: SvgNode) -> SvgNode? {
        guard let href = use.attr("href") ?? use.attr("xlink:href"), href.hasPrefix("#") else { return nil }
        return byId[String(href.dropFirst())]
    }

    // MARK: values

    private func color(_ value: String) -> CGColor {
        if let known = colors[value] { return known }
        let made = makeColor(value)
        if colors.count >= 512 { colors.removeAll(keepingCapacity: true) }
        colors[value] = made
        return made
    }

    private func makeColor(_ value: String) -> CGColor {
        func rgba(_ r: CGFloat, _ g: CGFloat, _ b: CGFloat, _ a: CGFloat) -> CGColor {
            CGColor(colorSpace: SvgCanvas.space, components: [r, g, b, a])!
        }
        switch value {
        case "white": return rgba(1, 1, 1, 1)
        case "black": return rgba(0, 0, 0, 1)
        case "transparent": return rgba(0, 0, 0, 0)
        default:
            guard value.hasPrefix("#") else { return rgba(0, 0, 0, 1) }
            var hex = String(value.dropFirst())
            if hex.count == 3 { hex = hex.map { "\($0)\($0)" }.joined() }
            let rgb = Int(hex, radix: 16) ?? 0
            return rgba(CGFloat((rgb >> 16) & 255) / 255, CGFloat((rgb >> 8) & 255) / 255, CGFloat(rgb & 255) / 255, 1)
        }
    }

    /// url(#id)
    private func urlId(_ url: String) -> String {
        let trimmed = url.trimmingCharacters(in: .whitespaces)
        return String(trimmed.dropFirst(5).dropLast(1))
    }

    private func number(_ s: String) -> CGFloat { CGFloat(Double(s) ?? 0) }

    private func numbers(_ s: String) -> [CGFloat] {
        s.split(whereSeparator: { $0 == " " || $0 == "," }).map { number(String($0)) }
    }

    /// transform="A B C": SVG applies C first, so the steps are concatenated in order.
    private func transforms(_ value: String) -> [CGAffineTransform] {
        var steps: [CGAffineTransform] = []
        // The groups the pattern (\w+)\s*\(([^)]*)\) matches, read without building a regex.
        let b = Array(value.utf8)
        let n = b.count
        func isWord(_ c: UInt8) -> Bool { (c >= 0x30 && c <= 0x39) || (c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A) || c == 0x5F }
        var i = 0
        while i < n {
            guard isWord(b[i]) else { i += 1; continue }
            let start = i
            while i < n, isWord(b[i]) { i += 1 }
            let end = i
            var j = i
            while j < n, b[j] == 0x20 || b[j] == 0x09 || b[j] == 0x0A || b[j] == 0x0D { j += 1 }
            guard j < n, b[j] == 0x28, let close = b[(j + 1)...].firstIndex(of: 0x29) else { continue }
            let name = String(decoding: b[start..<end], as: UTF8.self)
            let args = numbers(String(decoding: b[(j + 1)..<close], as: UTF8.self))
            i = close + 1
            switch name {
            case "translate": steps.append(CGAffineTransform(translationX: args[0], y: args.count > 1 ? args[1] : 0))
            case "scale": steps.append(CGAffineTransform(scaleX: args[0], y: args.count > 1 ? args[1] : args[0]))
            case "rotate":
                let angle = args[0] * .pi / 180
                if args.count >= 3 {
                    steps.append(CGAffineTransform(translationX: args[1], y: args[2]))
                    steps.append(CGAffineTransform(rotationAngle: angle))
                    steps.append(CGAffineTransform(translationX: -args[1], y: -args[2]))
                } else {
                    steps.append(CGAffineTransform(rotationAngle: angle))
                }
            case "matrix":
                steps.append(CGAffineTransform(a: args[0], b: args[1], c: args[2], d: args[3], tx: args[4], ty: args[5]))
            default:
                break
            }
        }
        return steps
    }

    private func apply(_ value: String, to ctx: CGContext) {
        for step in transforms(value) { ctx.concatenate(step) }
    }
}

/// Absolute-command SVG path data (M L H V C A Z) to a Core Graphics path.
enum SvgPath {
    static func parse(_ data: String) -> CGPath {
        let path = CGMutablePath()
        var reader = Reader(Array(data.utf8))
        var open = false
        var start = CGPoint.zero
        var current = CGPoint.zero
        var command: Character = " "
        while reader.more() {
            if reader.atCommand() { command = reader.command() } else if command == "M" { command = "L" }
            if command == "Z" {
                if open { path.closeSubpath() }
                open = false
                current = start
                continue
            }
            if command == "M" {
                current = reader.point()
                start = current
                path.move(to: start)
                open = true
                continue
            }
            if !open {
                start = current
                path.move(to: start)
                open = true
            }
            switch command {
            case "L":
                current = reader.point()
                path.addLine(to: current)
            case "H":
                current = CGPoint(x: reader.number(), y: current.y)
                path.addLine(to: current)
            case "V":
                current = CGPoint(x: current.x, y: reader.number())
                path.addLine(to: current)
            case "C":
                let c1 = reader.point(), c2 = reader.point()
                current = reader.point()
                path.addCurve(to: current, control1: c1, control2: c2)
            case "A":
                let rx = abs(reader.number()), ry = abs(reader.number())
                let rotation = reader.number()
                let large = reader.flag(), sweep = reader.flag()
                let end = reader.point()
                if rx == 0 || ry == 0 {
                    path.addLine(to: end)
                } else {
                    arc(path, from: current, to: end, rx: rx, ry: ry, rotation: rotation, large: large, sweep: sweep)
                }
                current = end
            default:
                break
            }
        }
        return path
    }

    /// An SVG elliptical arc as cubic Béziers (the W3C implementation notes, F.6.5).
    private static func arc(_ path: CGMutablePath, from p1: CGPoint, to p2: CGPoint, rx rxIn: CGFloat, ry ryIn: CGFloat,
                            rotation: CGFloat, large: Bool, sweep: Bool) {
        var rx = rxIn, ry = ryIn
        let phi = rotation * .pi / 180
        let cosPhi = cos(phi), sinPhi = sin(phi)
        let dx = (p1.x - p2.x) / 2, dy = (p1.y - p2.y) / 2
        let x1p = cosPhi * dx + sinPhi * dy
        let y1p = -sinPhi * dx + cosPhi * dy
        let lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
        if lambda > 1 {
            let s = lambda.squareRoot()
            rx *= s
            ry *= s
        }
        let numerator = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
        let denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p
        var coefficient = denominator == 0 ? 0 : max(0, numerator / denominator).squareRoot()
        if large == sweep { coefficient = -coefficient }
        let cxp = coefficient * rx * y1p / ry
        let cyp = -coefficient * ry * x1p / rx
        let cx = cosPhi * cxp - sinPhi * cyp + (p1.x + p2.x) / 2
        let cy = sinPhi * cxp + cosPhi * cyp + (p1.y + p2.y) / 2
        func angle(_ ux: CGFloat, _ uy: CGFloat, _ vx: CGFloat, _ vy: CGFloat) -> CGFloat {
            atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        }
        let theta1 = angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
        var delta = angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
        if !sweep && delta > 0 { delta -= 2 * .pi } else if sweep && delta < 0 { delta += 2 * .pi }
        let segments = max(1, Int((abs(delta) / (.pi / 2)).rounded(.up)))
        let step = delta / CGFloat(segments)
        let t = 4.0 / 3.0 * tan(step / 4)
        func map(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
            CGPoint(x: cosPhi * rx * x - sinPhi * ry * y + cx, y: sinPhi * rx * x + cosPhi * ry * y + cy)
        }
        for i in 0..<segments {
            let a1 = theta1 + CGFloat(i) * step, a2 = a1 + step
            let c1 = map(cos(a1) - t * sin(a1), sin(a1) + t * cos(a1))
            let c2 = map(cos(a2) + t * sin(a2), sin(a2) - t * cos(a2))
            path.addCurve(to: map(cos(a2), sin(a2)), control1: c1, control2: c2)
        }
    }

    /// Path data is ASCII; walking it as Characters was the slowest part of a frame.
    private struct Reader {
        let s: [UInt8]
        var i = 0
        init(_ s: [UInt8]) { self.s = s }

        private static func isDigit(_ c: UInt8) -> Bool { c >= 0x30 && c <= 0x39 }

        mutating func more() -> Bool {
            skip()
            return i < s.count
        }
        mutating func atCommand() -> Bool {
            skip()
            guard i < s.count else { return false }
            let c = s[i]
            return ((c >= 0x41 && c <= 0x5A) || (c >= 0x61 && c <= 0x7A)) && c != 0x65 && c != 0x45
        }
        mutating func command() -> Character {
            defer { i += 1 }
            return Character(UnicodeScalar(s[i]))
        }
        mutating func point() -> CGPoint {
            let x = number()
            return CGPoint(x: x, y: number())
        }
        mutating func flag() -> Bool {
            skip()
            defer { i += 1 }
            return s[i] == 0x31
        }
        mutating func number() -> CGFloat {
            skip()
            let start = i
            if i < s.count, s[i] == 0x2B || s[i] == 0x2D { i += 1 }
            while i < s.count, Reader.isDigit(s[i]) || s[i] == 0x2E { i += 1 }
            if i < s.count, s[i] == 0x65 || s[i] == 0x45 {
                i += 1
                if i < s.count, s[i] == 0x2B || s[i] == 0x2D { i += 1 }
                while i < s.count, Reader.isDigit(s[i]) { i += 1 }
            }
            return CGFloat(Double(String(decoding: s[start..<i], as: UTF8.self)) ?? 0)
        }
        private mutating func skip() {
            while i < s.count, s[i] == 0x20 || s[i] == 0x0A || s[i] == 0x09 || s[i] == 0x0D || s[i] == 0x2C { i += 1 }
        }
    }
}
