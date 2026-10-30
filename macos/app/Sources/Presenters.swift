// The two floating panels the drawer raises beside a character: its speech
// bubble and the hover card. Both are borderless, never take focus or clicks,
// and sit above the drawer.
import SwiftUI
import AppKit

final class FloatingPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    init(size: NSSize) {
        super.init(contentRect: NSRect(origin: .zero, size: size), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = NSWindow.Level(rawValue: NSWindow.Level.statusBar.rawValue + 1)
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        ignoresMouseEvents = true
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
    }
}

// MARK: Speech bubble

/// A character's line beside it. It hugs its text (240 at most), its pointer
/// or soft tail aims at the speaker, and it pops by kind: done hops, waiting
/// knocks twice, an error shakes, a plain line just pops. Reduced motion
/// fades only.
struct BubbleView: View {
    let line: String
    let kind: String
    let swatch: UInt32
    let palette: Palette
    let rightEdge: Bool
    let tailY: CGFloat
    @State private var appeared = false
    @State private var dx: CGFloat = 0
    @State private var dy: CGFloat = 0

    private var pixel: Bool { palette.skin == .pixel }
    static func reach(_ pixel: Bool) -> CGFloat { pixel ? 4 : 7 }

    /// A long Pixel line breaks between its phrases ("Can I run this?" / "Needs your OK."), not before a lone word.
    static func phrased(_ line: String, pixel: Bool) -> String {
        guard pixel, line.count > 24, !line.contains("\n") else { return line }
        for mark in ["? ", ". ", "! "] where line.contains(mark) {
            return line.replacingOccurrences(of: mark, with: String(mark.first!) + "\n")
        }
        return line
    }
    static func inset(_ pixel: Bool) -> CGFloat { pixel ? 4 : 20 }

    var body: some View {
        let reduced = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        let reach = BubbleView.reach(pixel)
        SkinText(pixel: pixel, text: BubbleView.phrased(line, pixel: pixel), pixelSize: 13, size: 13, color: palette.color("Text"), maxWidth: 240 - 28)
            .lineSpacing(pixel ? 0 : 2)
            .frame(minHeight: pixel ? 0 : 18)
            .multilineTextAlignment(.leading)
            .frame(maxWidth: 240 - 30, alignment: .leading)
            .fixedSize(horizontal: true, vertical: true)
            .padding(.horizontal, pixel ? 14 : 15).padding(.top, 9).padding(.bottom, 10)
            .background(plate)
            .padding(.leading, rightEdge ? 0 : reach).padding(.trailing, rightEdge ? reach : 0)
            .padding(BubbleView.inset(pixel))
            .offset(x: dx, y: dy)
            .scaleEffect(appeared || reduced ? 1 : 0.86, anchor: rightEdge ? .trailing : .leading)
            .opacity(appeared ? 1 : 0)
            .onAppear {
                withAnimation(reduced ? .easeOut(duration: 0.15) : .spring(response: 0.28, dampingFraction: 0.62)) { appeared = true }
                if !reduced { play() }
            }
    }

    @ViewBuilder private var plate: some View {
        if pixel {
            PixelPlateBackground(palette: palette, outline: palette.readable(swatch), fill: palette.color("Paper"),
                                 pointerY: tailY, pointLeft: !rightEdge)
        } else {
            ClassicPlate(palette: palette, radius: 12, tailY: tailY, pointsRight: rightEdge)
        }
    }

    /// The pop each kind of line gets.
    private func play() {
        func after(_ seconds: Double, _ change: @escaping () -> Void) {
            DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { withAnimation(.easeOut(duration: 0.07)) { change() } }
        }
        switch kind {
        case "done":
            after(0.05) { dy = -6 }
            after(0.16) { dy = 0 }
        case "waiting":
            for (index, offset) in [4.0, -3.0, 4.0, -2.0, 0.0].enumerated() {
                after(0.12 + Double(index) * 0.07) { dx = CGFloat(offset) * (rightEdge ? -1 : 1) }
            }
        case "error":
            for (index, offset) in [5.0, -5.0, 4.0, -4.0, 2.0, 0.0].enumerated() {
                after(0.05 + Double(index) * 0.05) { dx = CGFloat(offset) }
            }
        default:
            break
        }
    }
}

final class BubblePresenter {
    private struct Open {
        let panel: FloatingPanel
        var timer: Timer?
        let closed: () -> Void
    }

    private var open: [String: Open] = [:]

    func say(id: String, line: String, kind: String, swatch: UInt32, pixel: Bool, rightEdge: Bool, anchor: NSRect, area: NSRect,
             linger: Double?, dark: Bool, closed: @escaping () -> Void) {
        hide(id, animated: false)
        let palette = Palette(skin: pixel ? .pixel : .classic, dark: dark)
        // Measure with the tail at the middle, then place it on the character.
        let probe = NSHostingView(rootView: BubbleView(line: line, kind: kind, swatch: swatch, palette: palette, rightEdge: rightEdge, tailY: 0))
        let size = probe.fittingSize
        let place = BubblePresenter.placement(size: size, anchor: anchor, area: area, rightEdge: rightEdge, pixel: pixel)
        let view = NSHostingView(rootView: BubbleView(line: line, kind: kind, swatch: swatch, palette: palette, rightEdge: rightEdge, tailY: place.tailY))
        let panel = FloatingPanel(size: size)
        panel.contentView = view
        panel.setFrame(NSRect(origin: place.origin, size: size), display: true)
        panel.orderFrontRegardless()
        let timer = Timer.scheduledTimer(withTimeInterval: linger ?? 4.5, repeats: false) { [weak self] _ in self?.hide(id, animated: true) }
        open[id] = Open(panel: panel, timer: timer, closed: closed)
    }

    /// Where a bubble goes beside its character (AppKit screen coordinates), and where its pointer aims.
    static func placement(size: NSSize, anchor: NSRect, area: NSRect, rightEdge: Bool, pixel: Bool) -> (origin: CGPoint, tailY: CGFloat) {
        let inset = BubbleView.inset(pixel)
        let gap: CGFloat = 10
        let bodyHeight = size.height - 2 * inset
        var x = rightEdge ? anchor.minX - gap - size.width + inset : anchor.maxX + gap - inset
        var y = anchor.midY - size.height / 2
        x = min(max(x, area.minX - inset), area.maxX - size.width + inset)
        y = min(max(y, area.minY - inset), area.maxY - size.height + inset)
        // The tail points at the character's centre, measured from the bubble body's top.
        let centreFromTop = (y + size.height - inset) - anchor.midY
        return (CGPoint(x: x, y: y), min(max(centreFromTop, 14), max(14, bodyHeight - 14)))
    }

    func hide(_ id: String, animated: Bool) {
        guard let entry = open.removeValue(forKey: id) else { return }
        entry.timer?.invalidate()
        let panel = entry.panel
        let finish = { panel.orderOut(nil); entry.closed() }
        if animated {
            NSAnimationContext.runAnimationGroup({ context in
                context.duration = 0.18
                panel.animator().alphaValue = 0
            }, completionHandler: finish)
        } else {
            finish()
        }
    }

    func hideAll(animated: Bool) {
        for id in Array(open.keys) { hide(id, animated: animated) }
    }
}

// MARK: Hover card

final class CardPresenter {
    private var panel: FloatingPanel?

    var isOpen: Bool { panel?.isVisible == true }

    /// Sections stand in as few columns as fit the room, cut so the tallest is as short as it can be.
    static func columns(_ model: CardsModel, pixel: Bool, area: NSRect, anchor: NSRect, rightEdge: Bool) -> [[Int]] {
        let count = model.cards.count
        let all = Array(0..<count)
        guard count > 1 else { return [all] }
        func estimate(_ card: CardModel) -> CGFloat {
            var h: CGFloat = 40
            if card.tasks != nil { h += 22 }
            h += CGFloat(card.waiting.rows.count) * 18 + (card.waiting.more > 0 ? 16 : 0)
            h += CGFloat(card.meters.count) * 22
            if card.tokens != nil { h += 40 }
            h += CGFloat(card.note.count) * 34
            return h
        }
        let heights = model.cards.map(estimate)
        let divider: CGFloat = pixel ? 2 * 9 + 2 : 2 * 10 + 1
        func tall(_ range: Range<Int>) -> CGFloat { range.map { heights[$0] }.reduce(0, +) + CGFloat(max(0, range.count - 1)) * divider }
        let room = area.height - 12 - (pixel ? 28 : 26)
        if tall(0..<count) <= room { return [all] }
        let beside = rightEdge ? anchor.minX - 10 - area.minX : area.maxX - anchor.maxX - 10
        let column: CGFloat = (pixel ? 272 : 264) - 32
        var best: [[Int]]?
        var shortest = CGFloat.greatestFiniteMagnitude
        var columnsCount = 2
        while columnsCount <= count {
            if 32 + CGFloat(columnsCount) * column + CGFloat(columnsCount - 1) * 33 > beside - 6 { break }
            // Greedy even cut: walk the sections, closing a column once it reaches the target height.
            let target = tall(0..<count) / CGFloat(columnsCount)
            var cut: [[Int]] = [[]]
            var running: CGFloat = 0
            for index in 0..<count {
                if running >= target && cut.count < columnsCount { cut.append([]); running = 0 }
                cut[cut.count - 1].append(index)
                running += heights[index] + divider
            }
            let tallest = cut.map { $0.map { heights[$0] }.reduce(0, +) }.max() ?? 0
            if tallest < shortest { shortest = tallest; best = cut }
            if tallest <= room { break }
            columnsCount += 1
        }
        return best ?? [all]
    }

    func show(_ model: CardsModel, pixel: Bool, rightEdge: Bool, anchor: NSRect, area: NSRect, dark: Bool) {
        guard !model.cards.isEmpty else { hide(animated: false); return }
        let palette = Palette(skin: pixel ? .pixel : .classic, dark: dark)
        let columns = CardPresenter.columns(model, pixel: pixel, area: area, anchor: anchor, rightEdge: rightEdge)
        let probe = NSHostingView(rootView: CardView(model: model, palette: palette, columns: columns, tailY: nil, rightEdge: rightEdge))
        let size = probe.fittingSize
        let place = CardPresenter.placement(size: size, anchor: anchor, area: area, rightEdge: rightEdge, pixel: pixel)
        let view = NSHostingView(rootView: CardView(model: model, palette: palette, columns: columns, tailY: place.tailY, rightEdge: rightEdge))
        let panel = self.panel ?? FloatingPanel(size: size)
        self.panel = panel
        panel.contentView = view
        panel.setFrame(NSRect(origin: place.origin, size: size), display: true)
        panel.alphaValue = 1
        panel.orderFrontRegardless()
    }

    /// Where a card goes beside its character (AppKit screen coordinates), and where its pointer aims.
    static func placement(size: NSSize, anchor: NSRect, area: NSRect, rightEdge: Bool, pixel: Bool) -> (origin: CGPoint, tailY: CGFloat) {
        let inset = CardMetrics.inset(pixel)
        let gap: CGFloat = 10
        var x = rightEdge ? anchor.minX - gap - size.width + inset : anchor.maxX + gap - inset
        var y = anchor.midY - size.height / 2
        x = min(max(x, area.minX - inset), area.maxX - size.width + inset)
        y = min(max(y, area.minY - inset), area.maxY - size.height + inset)
        let bodyHeight = size.height - 2 * inset
        let centreFromTop = (y + size.height - inset) - anchor.midY
        return (CGPoint(x: x, y: y), min(max(centreFromTop, 16), max(16, bodyHeight - 16)))
    }

    func hide(animated: Bool) {
        guard let panel, panel.isVisible else { return }
        if animated {
            NSAnimationContext.runAnimationGroup({ context in
                context.duration = 0.15
                panel.animator().alphaValue = 0
            }, completionHandler: { panel.orderOut(nil); panel.alphaValue = 1 })
        } else {
            panel.orderOut(nil)
        }
    }
}
