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

struct BubbleView: View {
    let line: String
    let kind: String
    let swatch: UInt32
    let palette: Palette
    let rightEdge: Bool
    let tailY: CGFloat
    @State private var appeared = false

    private var pixel: Bool { palette.skin == .pixel }
    static let reach: CGFloat = 7
    static let room: EdgeInsets = EdgeInsets(top: 12, leading: 16, bottom: 24, trailing: 16)

    var body: some View {
        Text(line)
            .font(pixel ? PixelFace.font(13) : .system(size: 13))
            .foregroundColor(palette.color("Text"))
            .multilineTextAlignment(.leading)
            .frame(maxWidth: 240 - 30, alignment: .leading)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, pixel ? 14 : 15).padding(.top, 9).padding(.bottom, 10)
            .background(plate)
            .padding(.leading, rightEdge ? 0 : BubbleView.reach).padding(.trailing, rightEdge ? BubbleView.reach : 0)
            .padding(BubbleView.room)
            .overlay(alignment: .topLeading) { tail }
            .scaleEffect(appeared || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion ? 1 : 0.86, anchor: rightEdge ? .trailing : .leading)
            .opacity(appeared ? 1 : 0)
            .onAppear { withAnimation(.spring(response: 0.28, dampingFraction: 0.62)) { appeared = true } }
    }

    @ViewBuilder private var plate: some View {
        if pixel {
            PixelPlateBackground(palette: palette, outline: palette.readable(swatch), fill: palette.color("Paper"))
        } else {
            RoundedRectangle(cornerRadius: 10)
                .fill(palette.color("Paper"))
                .overlay(RoundedRectangle(cornerRadius: 10).stroke(palette.hairline, lineWidth: 1))
                .shadow(color: .black.opacity(palette.dark ? 0.45 : 0.18), radius: 8, x: 0, y: 3)
        }
    }

    @ViewBuilder private var tail: some View {
        GeometryReader { proxy in
            let x = rightEdge ? proxy.size.width - BubbleView.room.trailing - BubbleView.reach / 2 : BubbleView.room.leading + BubbleView.reach / 2
            if pixel {
                PixelPointer(palette: palette, rightEdge: rightEdge).position(x: x, y: tailY + BubbleView.room.top)
            } else {
                Triangle(rightEdge: rightEdge).fill(palette.color("Paper")).frame(width: 8, height: 12).position(x: x, y: tailY + BubbleView.room.top)
            }
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
        let inner = NSRect(x: BubbleView.room.leading, y: BubbleView.room.bottom,
                           width: size.width - BubbleView.room.leading - BubbleView.room.trailing - BubbleView.reach,
                           height: size.height - BubbleView.room.top - BubbleView.room.bottom)
        let gap: CGFloat = 10
        var x = rightEdge ? anchor.minX - gap - size.width + BubbleView.room.trailing : anchor.maxX + gap - BubbleView.room.leading
        var y = anchor.midY - size.height / 2 - (BubbleView.room.bottom - BubbleView.room.top) / 2
        x = min(max(x, area.minX - BubbleView.room.leading), area.maxX - size.width + BubbleView.room.trailing)
        y = min(max(y, area.minY - BubbleView.room.bottom), area.maxY - size.height + BubbleView.room.top)
        // The tail points at the character's centre, measured from the bubble body's top.
        let centreFromTop = (y + size.height) - anchor.midY - BubbleView.room.top
        let tailY = min(max(centreFromTop, 14), max(14, inner.height - 14))
        let view = NSHostingView(rootView: BubbleView(line: line, kind: kind, swatch: swatch, palette: palette, rightEdge: rightEdge, tailY: tailY))
        let panel = FloatingPanel(size: size)
        panel.contentView = view
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
        panel.orderFrontRegardless()
        let timer = Timer.scheduledTimer(withTimeInterval: linger ?? 4.5, repeats: false) { [weak self] _ in self?.hide(id, animated: true) }
        open[id] = Open(panel: panel, timer: timer, closed: closed)
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
        let inset: CGFloat = pixel ? 4 : 20
        let gap: CGFloat = 10
        var x = rightEdge ? anchor.minX - gap - size.width + inset : anchor.maxX + gap - inset
        var y = anchor.midY - size.height / 2
        x = min(max(x, area.minX - inset), area.maxX - size.width + inset)
        y = min(max(y, area.minY - inset), area.maxY - size.height + inset)
        let bodyHeight = size.height - 2 * inset
        let centreFromTop = (y + size.height - inset) - anchor.midY
        let tailY = min(max(centreFromTop, 16), max(16, bodyHeight - 16))
        let view = NSHostingView(rootView: CardView(model: model, palette: palette, columns: columns, tailY: tailY, rightEdge: rightEdge))
        let panel = self.panel ?? FloatingPanel(size: size)
        self.panel = panel
        panel.contentView = view
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
        panel.alphaValue = 1
        panel.orderFrontRegardless()
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
