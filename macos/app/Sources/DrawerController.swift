// The edge drawer: a transparent panel on one edge of the chosen display, one
// round slot per shown character, the game loop that steps the engines, and the
// pointer (hover card, click, drag along the edge, right-click menu). The
// rules are the Windows drawer's (DrawerWindow.*.cs); what Windows does with a
// moment's choreography (the drawer sliding in, lifting off the edge when
// dragged) is left to a later pass.
import AppKit

enum Metrics {
    static let botSize: CGFloat = 44
    static let gap: CGFloat = 10
    static let edgeInset: CGFloat = 6
    static let innerInset: CGFloat = 6
    static let verticalPadding: CGFloat = 12
    static let gazeRange: CGFloat = 480
    static let dragThreshold: CGFloat = 4
    static let fidgetGap: Double = 50
    static let firstFidgetAfter: Double = 12
    static let width: CGFloat = innerInset + botSize + edgeInset
}

/// A character's animation state, owned by the game loop.
final class Slot {
    var id = ""
    var kind = ""
    var look = ""
    var swatch: UInt32 = 0
    var seq = -1
    var engineState = "idle"
    var hold = "idle"
    var svg: String?
    var image: CGImage?
    var imageSvg: String?
    var imageScale: CGFloat = 0
    var gazing = false
    var lastStep = 0.0
    var nextStep = 0.0
    var fidgets: [String] = []
    var fidget: String?
    var fidgetUntil = 0.0
    var nextFidget = 0.0
    var resting = false
    var since = 0.0
    var visible = false
    var bounds = CGRect.zero
}

private let calmStates: Set<String> = ["idle", "listening", "broken", "offline", "sleeping", "locked", "resting"]

final class DrawerPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

final class DrawerController {
    let panel: DrawerPanel
    let view: DrawerView
    private(set) var frame: FrameModel?
    private var engine: CharacterEngine?
    private var engineTheme = ""
    private var slots: [Slot] = []
    private var loopTimer: Timer?
    private var paceFps = 0.0
    private let canvas = SvgCanvas()
    private let clock = Date()
    private var away = false
    private var reducedMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion

    let cards = CardPresenter()
    let bubbles = BubblePresenter()
    var openSettings: ((String?) -> Void)?
    var showMenu: ((String, NSPoint) -> Void)?

    private var hoverBot: String?
    private var cardIntent: Timer?
    private var cardClose: Timer?
    private var cardHeldBack = false
    private var pressed: String?
    private var pressY: CGFloat = 0
    private var pressTop: CGFloat = 0
    private var dragging = false
    private var refused = false
    private(set) var screen: NSScreen?

    init() {
        panel = DrawerPanel(contentRect: NSRect(x: 0, y: 0, width: Metrics.width, height: 100),
                            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        view = DrawerView(frame: NSRect(x: 0, y: 0, width: Metrics.width, height: 100))
        panel.contentView = view
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.isMovable = false
        panel.acceptsMouseMovedEvents = true
        panel.isReleasedWhenClosed = false
        panel.title = "Edgelings"
        view.controller = self

        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            self?.layout()
        }
        NotificationCenter.default.addObserver(forName: NSWorkspace.accessibilityDisplayOptionsDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
            self?.reducedMotion = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        }
    }

    // MARK: Frames from Core

    func apply(_ next: FrameModel) {
        let previous = frame
        frame = next
        if engine == nil || engineTheme != next.theme { buildEngine(for: next) }
        if slots.count != next.bots.count { slots = next.bots.map { _ in Slot() } }
        let now = clock.timeIntervalSinceNow * -1
        for (index, bot) in next.bots.enumerated() {
            let slot = slots[index]
            let known = slot.id == bot.id
            if !known {
                slot.id = bot.id
                slot.kind = bot.kind
                slot.nextFidget = Metrics.firstFidgetAfter + Double.random(in: 0..<Metrics.fidgetGap)
            }
            slot.swatch = bot.swatch
            if slot.look != bot.look {
                if known, engine != nil {
                    if !next.isPixel && !reducedMotion && panel.isVisible && !slot.look.isEmpty {
                        // M20: the bot turns edge-on like a coin and its new look swaps in there.
                        let target = bot.look
                        play(DrawerMoves.flip(newMoment(), index, reduced: false) { [weak self] in self?.engine?.setLook(index, target) })
                    } else {
                        engine?.setLook(index, bot.look)
                    }
                }
                slot.look = bot.look
                slot.fidgets = engine?.fidgets(kind: bot.kind, look: bot.look) ?? []
                slot.gazing = false
            }
            if slot.seq != bot.seq {
                slot.seq = bot.seq
                show(index, bot, now: now)
            }
            if bot.takeBack == true { bubbles.hide(bot.id, animated: true) }
            if let speech = bot.speech { say(index, bot, speech) }
        }
        if previous == nil || previous!.side != next.side || previous!.order != next.order
            || previous!.shownBots.map(\.id) != next.shownBots.map(\.id) || previous!.offset != next.offset
            || previous!.locked != next.locked || previous!.pinnedTop != next.pinnedTop || previous!.display != next.display {
            layout()
        }
        updatePace()
    }

    private func buildEngine(for frame: FrameModel) {
        engine = nil
        do {
            let built = try CharacterEngine(theme: frame.theme, directory: AppController.enginesDirectory())
            engine = built
            engineTheme = frame.theme
            for (index, bot) in frame.bots.enumerated() {
                let added = built.addBot(kind: bot.kind, tempo: bot.tempo, seed: 7 + index * 13, look: bot.look)
                if added != index { NSLog("Edgelings: engine bot index \(added) for \(index)") }
            }
            for slot in slots { slot.look = ""; slot.seq = -1; slot.svg = nil; slot.image = nil; slot.engineState = "idle"; slot.hold = "idle" }
        } catch {
            NSLog("Edgelings: engine \(frame.theme) failed: \(error)")
            AppController.shared.request("log", ["text": "engine \(frame.theme) did not load"])
            engineTheme = frame.theme
        }
    }

    /// Puts a character into a state; a resting one takes the set's resting
    /// state, or in a set without one idles.
    private func show(_ index: Int, _ bot: BotModel, now: Double) {
        let slot = slots[index]
        var entry = bot.entry
        var hold = bot.hold
        if bot.resting, engine?.draws("resting") == true { entry = "resting"; hold = "resting" }
        slot.hold = hold
        slot.resting = bot.resting
        slot.since = now
        slot.fidget = nil
        slot.nextFidget = slot.since + Metrics.fidgetGap * (0.5 + Double.random(in: 0..<1))
        let still = holdsStill(entry, look: slot.look)
        engine?.setState(index, entry, phase: bot.oneShot || still ? 0 : -1)
        if engine == nil { slot.engineState = hold }
        slot.nextStep = 0
    }

    private func holdsStill(_ state: String, look: String) -> Bool {
        guard reducedMotion else { return false }
        if state == "resting" { return true }
        guard frame?.isPixel == true else { return false }
        return state == "sleeping" || (state == "offline" && ["puff", "terminal", "astronaut"].contains(look))
    }

    // MARK: Layout

    /// The chosen display (a CGDirectDisplayID as text), else the one with the menu bar.
    private func chooseScreen() -> NSScreen? {
        if let choice = frame?.display, choice != "auto",
           let match = NSScreen.screens.first(where: { DrawerController.displayId($0) == choice }) { return match }
        return NSScreen.screens.first ?? NSScreen.main
    }

    static func displayId(_ screen: NSScreen) -> String {
        ((screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.stringValue) ?? ""
    }

    private func windowHeight(_ count: Int) -> CGFloat {
        Metrics.verticalPadding * 2 + CGFloat(count) * Metrics.botSize + CGFloat(max(0, count - 1)) * Metrics.gap
    }

    /// A roster moment (M2) is playing: the panel covers the old and the new column until it ends.
    private var rosterBusy = false
    private var layoutPending = false

    func layout() {
        guard let frame, let screen = chooseScreen() else { return }
        if rosterBusy { layoutPending = true; return }
        self.screen = screen
        let shown = frame.shownBots
        let ids = Set(shown.map(\.id))
        let height = windowHeight(shown.count)
        let work = screen.visibleFrame
        let travel = max(0, work.height - height)
        var topFromWork = CGFloat(frame.offset) * travel
        if frame.locked, let pin = frame.pinnedTop { topFromWork = min(max(CGFloat(pin), 0), travel) }
        let x = frame.side == "right" ? work.maxX - Metrics.width : work.minX
        let y = work.maxY - topFromWork - height
        let visibleBefore = Set(slots.filter { $0.visible }.map(\.id))
        if panel.isVisible, !reducedMotion, engine != nil, !visibleBefore.isEmpty, !ids.isEmpty, visibleBefore != ids,
           moment?.holds != true, abs(panel.frame.minX - x) < 1 {
            changeRoster(shown: shown, ids: ids, visibleBefore: visibleBefore, newFrame: NSRect(x: x, y: y, width: Metrics.width, height: height))
            return
        }
        for slot in slots { slot.visible = ids.contains(slot.id) }
        for (row, bot) in shown.enumerated() {
            if let slot = slots.first(where: { $0.id == bot.id }) {
                slot.bounds = CGRect(x: Metrics.innerInset, y: Metrics.verticalPadding + CGFloat(row) * (Metrics.botSize + Metrics.gap),
                                     width: Metrics.botSize, height: Metrics.botSize)
            }
        }
        panel.setFrame(NSRect(x: x, y: y, width: Metrics.width, height: height), display: true)
        view.needsDisplay = true
        if shown.isEmpty { panel.orderOut(nil) }
        else if !panel.isVisible && AppController.shared.drawerShown {
            if !reducedMotion && engine != nil && !hasRevealed {
                hasRevealed = true
                play(DrawerMoves.reveal(newMoment(), topDown))
            }
            panel.orderFrontRegardless()
        }
    }

    /// M2: bots leave, the rest close the gap or make room, and newcomers slide in. The panel covers the
    /// old and the new column meanwhile, so every bot starts where it was on screen; it settles on the new one after.
    private func changeRoster(shown: [BotModel], ids: Set<String>, visibleBefore: Set<String>, newFrame: NSRect) {
        let old = panel.frame
        let union = old.union(newFrame)
        let oldOffset = union.maxY - old.maxY
        let newOffset = union.maxY - newFrame.maxY
        var rest: [String: CGRect] = [:]
        for (row, bot) in shown.enumerated() {
            rest[bot.id] = CGRect(x: Metrics.innerInset, y: Metrics.verticalPadding + CGFloat(row) * (Metrics.botSize + Metrics.gap) + newOffset,
                                  width: Metrics.botSize, height: Metrics.botSize)
        }
        var leaving: [Int] = [], entering: [Int] = []
        var dy: [Int: Double] = [:]
        for (index, slot) in slots.enumerated() {
            let wasShown = visibleBefore.contains(slot.id), willShow = ids.contains(slot.id)
            // Where the bot is drawn now, in the union's coordinates (a moment already moving it counts).
            let drawnOld = slot.bounds.offsetBy(dx: 0, dy: wasShown ? oldOffset : 0)
            if wasShown && !willShow {
                slot.bounds = drawnOld
                leaving.append(index)
            } else if wasShown, let target = rest[slot.id] {
                dy[index] = Double(drawnOld.minY - target.minY) + Double(onScreenNow?(index)?.y ?? 0)
                slot.bounds = target
            } else if willShow, let target = rest[slot.id] {
                slot.bounds = target
                entering.append(index)
                slot.visible = true
            }
        }
        rosterBusy = true
        panel.setFrame(union, display: true)
        let ordered = topDown.filter { entering.contains($0) }
        let made = DrawerMoves.roster(newMoment(), leaving: leaving, dy: dy, entering: ordered, from: onScreenNow)
        cards.hide(animated: false)
        bubbles.hideAll(animated: false)
        play(made) { [weak self] in
            guard let self else { return }
            self.rosterBusy = false
            for index in leaving { self.slots[index].visible = false }
            self.layoutPending = false
            self.layout()
        }
    }

    func setShown(_ shown: Bool) {
        AppController.shared.drawerShown = shown
        if shown {
            layout()
            if !(frame?.shownBots.isEmpty ?? true) {
                if !panel.isVisible && !reducedMotion && engine != nil {
                    // M1: the bots slide in from beyond the edge.
                    moment = nil
                    let entering = DrawerMoves.reveal(newMoment(), topDown)
                    play(entering)
                } else {
                    moment = nil
                }
                panel.orderFrontRegardless()
            }
        } else {
            cards.hide(animated: false)
            bubbles.hideAll(animated: false)
            if panel.isVisible && !reducedMotion && engine != nil {
                // M1: the bots leave toward the edge, then the panel goes.
                play(DrawerMoves.hide(newMoment(holds: true), topDown, staggered: !(frame?.isPixel ?? true), from: onScreenNow)) { [weak self] in
                    self?.panel.orderOut(nil)
                    self?.moment = nil
                    self?.updatePace()
                }
            } else {
                panel.orderOut(nil)
            }
        }
        updatePace()
    }

    var isShown: Bool { panel.isVisible }

    /// A lock, a sleep or blanked displays rest the loop.
    func setAway(_ value: Bool) {
        away = value
        updatePace()
    }

    // MARK: The loop

    /// Busy characters step at the user's ceiling, calm ones whose eyes follow a
    /// nearby pointer at 60 (Pixel: 30), the rest breathe at 30. Reduced motion keeps 12 / 6.
    private func updatePace() {
        guard let frame, panel.isVisible, !away, engine != nil else { stopLoop(); return }
        let pixel = frame.isPixel
        var cap = Double(frame.fps)
        if frame.saveBattery && DrawerController.onBattery { cap = cap == 0 ? 30 : min(cap, 30) }
        let active = pixel ? (cap == 0 ? 30 : min(cap, 30)) : (cap == 0 ? 60 : cap)
        let busy = slots.contains { $0.visible && !calmStates.contains($0.engineState) }
        let near = !reducedMotion && pointerNear
        let moving = moment.map { $0.moving((clockSeconds - $0.start) * 1000) } ?? false
        let fps = moving ? active : busy ? (reducedMotion ? 12 : active) : near ? (pixel ? 30 : 60) : (reducedMotion ? 6 : 30)
        if fps == paceFps, loopTimer != nil { return }
        paceFps = fps
        loopTimer?.invalidate()
        let timer = Timer(timeInterval: 1 / fps, repeats: true) { [weak self] _ in self?.tick() }
        timer.tolerance = 0.2 / fps
        RunLoop.main.add(timer, forMode: .common)
        loopTimer = timer
    }

    private func stopLoop() {
        loopTimer?.invalidate()
        loopTimer = nil
        paceFps = 0
    }

    private var hasRevealed = false
    private var pointerNear = false

    // MARK: Moments (M1, M6, M20)

    /// The drawer moment playing, or held (bots lifted by a drag, gone while hidden).
    private(set) var moment: DrawerMoment?
    private var afterMoment: (() -> Void)?
    /// Classic's locked-drag tug, in points.
    private var tug = 0.0

    var edge: Double { frame?.side == "left" ? -1 : 1 }
    private var clockSeconds: Double { clock.timeIntervalSinceNow * -1 }

    /// The slots a moment moves, top first.
    private var topDown: [Int] {
        slots.enumerated().filter { $0.element.visible }.sorted { $0.element.bounds.minY < $1.element.bounds.minY }.map { $0.offset }
    }

    private func newMoment(holds: Bool = false) -> DrawerMoment {
        let made = DrawerMoment(start: clockSeconds, pixel: frame?.isPixel ?? true, edge: edge)
        made.holds = holds
        return made
    }

    /// The pose on screen now, for a moment that takes over from another.
    private var onScreenNow: DrawerMoves.OnScreen? {
        guard let current = moment else { return nil }
        return current.onScreen(at: (clockSeconds - current.start) * 1000)
    }

    /// Plays a moment; one it interrupts finishes what it owes first.
    private func play(_ next: DrawerMoment, then: (() -> Void)? = nil) {
        moment?.runRest()
        let owed = afterMoment
        afterMoment = nil
        owed?()
        moment = next
        afterMoment = then
        updatePace()
        view.needsDisplay = true
    }

    /// The pose a slot is drawn in now.
    func pose(of slot: Slot) -> BotPose {
        guard let current = moment, let index = slots.firstIndex(where: { $0 === slot }) else { return .rest }
        var pose = current.pose(index, (clockSeconds - current.start) * 1000)
        if current.tug { pose.y += tug }
        return pose
    }

    private func runMoment(_ now: Double) {
        guard let current = moment else { return }
        let ms = (now - current.start) * 1000
        current.runDue(ms)
        if ms >= current.duration, !current.ended {
            current.ended = true
            if !current.holds { moment = nil }
            let done = afterMoment
            afterMoment = nil
            done?()
            updatePace()
        }
        view.needsDisplay = true
    }

    private func tick() {
        guard let engine, let frame else { return }
        let now = clock.timeIntervalSinceNow * -1
        let pixel = frame.isPixel
        let reduced = reducedMotion
        runMoment(now)
        var due: [Int] = []
        for (index, slot) in slots.enumerated() where slot.visible {
            if now >= slot.nextStep - 0.004 { due.append(index) }
        }
        guard !due.isEmpty else { return }
        let pointer = NSEvent.mouseLocation
        let origin = panel.frame.origin
        var anyNear = false
        var dts = [Double?](repeating: nil, count: slots.count)
        var cap = Double(frame.fps)
        if frame.saveBattery && DrawerController.onBattery { cap = cap == 0 ? 30 : min(cap, 30) }
        let activeFps = reduced ? 12 : pixel ? (cap == 0 ? 30 : min(cap, 30)) : (cap == 0 ? 60 : cap)
        let gazeFps: Double = reduced ? 12 : pixel ? 30 : 60
        let calmFps: Double = reduced ? 6 : 30
        for index in due {
            let slot = slots[index]
            // The pointer relative to this bot's centre, in points; the view is flipped.
            let centreX = origin.x + slot.bounds.midX
            let centreY = origin.y + panel.frame.height - slot.bounds.midY
            let dx = pointer.x - centreX
            let dy = centreY - pointer.y
            let near = !reduced && hypot(dx, dy) < Metrics.gazeRange
            anyNear = anyNear || near
            if near {
                engine.setGaze(index, dx: Double(dx), dy: Double(dy))
                slot.gazing = true
            } else if slot.gazing {
                engine.clearGaze(index)
                slot.gazing = false
            }
            let calm = calmStates.contains(slot.engineState)
            let fps = !calm ? activeFps : near ? gazeFps : calmFps
            let interval = 1 / fps
            if holdsStill(slot.engineState, look: slot.look) && now - slot.since > 0.5 {
                dts[index] = nil
            } else {
                dts[index] = min(now - slot.lastStep, max(0.1, 1.5 * interval))
            }
            slot.lastStep = now
            slot.nextStep = now - slot.nextStep > interval ? now + interval : slot.nextStep + interval
        }
        pointerNear = anyNear
        // The pointer resting on a character opens its card even where the
        // window server sent no mouse-moved event (a non-activating panel).
        if pressed == nil { pollHover(pointer) }
        let frames = engine.tick(dts, size: Double(Metrics.botSize))
        for index in due where index < frames.count {
            guard let produced = frames[index] else { continue }
            let slot = slots[index]
            // Self-completing entries (finished, spawning…) fall back to idle;
            // keep playing the agent's phase instead.
            if produced.state == "idle" && slot.hold != "idle" { engine.setState(index, slot.hold, phase: -1) }
            slot.engineState = produced.state
            slot.svg = produced.svg
            fidget(index, slot, now: now, reduced: reduced)
        }
        view.needsDisplay = true
        updatePace()
    }

    /// Now and then an idle character does one of its own little things, and settles back.
    private func fidget(_ index: Int, _ slot: Slot, now: Double, reduced: Bool) {
        guard let engine, let frame else { return }
        if slot.fidget != nil {
            if now < slot.fidgetUntil { return }
            slot.fidget = nil
            slot.nextFidget = now + Metrics.fidgetGap * (0.5 + Double.random(in: 0..<1))
            if slot.hold == "idle" { engine.setState(index, "idle", phase: -1) }
            return
        }
        if reduced || slot.resting || slot.fidgets.isEmpty || slot.hold != "idle" || slot.engineState != "idle" || now < slot.nextFidget { return }
        let chosen = slot.fidgets[Int.random(in: 0..<slot.fidgets.count)]
        slot.fidget = chosen
        slot.fidgetUntil = now + 2.5 + Double.random(in: 0..<2)
        engine.setState(index, chosen, phase: frame.isPixel ? 0 : -1)
    }

    // MARK: Drawing

    /// A frame's SVG is rasterised once into a bitmap at the display's scale;
    /// every redraw only blits it.
    func image(for slot: Slot, scale: CGFloat) -> CGImage? {
        guard let svg = slot.svg else { return nil }
        if slot.imageSvg == svg, slot.imageScale == scale, let image = slot.image { return image }
        let pixels = Int((Metrics.botSize * scale).rounded())
        let space = CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: pixels * 4,
                                  space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.clear(CGRect(x: 0, y: 0, width: pixels, height: pixels))
        ctx.translateBy(x: 0, y: CGFloat(pixels))
        ctx.scaleBy(x: scale, y: -scale)
        try? canvas.render(svg: svg, into: ctx, antialias: frame?.isPixel != true)
        slot.image = ctx.makeImage()
        slot.imageSvg = svg
        slot.imageScale = scale
        return slot.image
    }

    var drawSlots: [Slot] { slots.filter { $0.visible } }
    var isPixel: Bool { frame?.isPixel ?? true }

    // MARK: Pointer

    func slot(at point: NSPoint) -> Slot? {
        slots.first { slot in
            guard slot.visible else { return false }
            let dx = point.x - slot.bounds.midX
            let dy = point.y - slot.bounds.midY
            return dx * dx + dy * dy <= Metrics.botSize * Metrics.botSize / 4
        }
    }

    func pointerMoved(_ point: NSPoint) {
        if pressed != nil { drag(); return }
        if cardHeldBack { return }
        guard let slot = slot(at: point) else { cardIntent?.invalidate(); return }
        if cards.isOpen {
            if hoverBot != slot.id { openCard(slot.id) }
        } else if cardIntent == nil || !(cardIntent!.isValid) {
            cardIntent = Timer.scheduledTimer(withTimeInterval: 0.2, repeats: false) { [weak self] _ in self?.openCardIfStillHovered() }
        }
    }

    private func pollHover(_ pointer: NSPoint) {
        if panel.frame.contains(pointer), slot(at: pointerInView()) != nil {
            pointerMoved(pointerInView())
        } else if cards.isOpen, hoverBot != nil, cardClose == nil || !(cardClose!.isValid) {
            cardIntent?.invalidate()
            cardClose = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: false) { [weak self] _ in self?.closeCardUnlessHovered() }
        }
    }

    func pointerEntered() { cardHeldBack = false; cardClose?.invalidate() }

    func pointerExited() {
        cardIntent?.invalidate()
        cardHeldBack = false
        if pressed == nil {
            cardClose?.invalidate()
            cardClose = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: false) { [weak self] _ in self?.closeCardUnlessHovered() }
        }
    }

    private func pointerInView() -> NSPoint {
        let screenPoint = NSEvent.mouseLocation
        let origin = panel.frame.origin
        return NSPoint(x: screenPoint.x - origin.x, y: panel.frame.height - (screenPoint.y - origin.y))
    }

    private func openCardIfStillHovered() {
        cardIntent?.invalidate()
        if pressed == nil, !cardHeldBack, let slot = slot(at: pointerInView()) { openCard(slot.id) }
    }

    private func closeCardUnlessHovered() {
        cardClose?.invalidate()
        if let slot = slot(at: pointerInView()), hoverBot != nil {
            if slot.id != hoverBot { openCard(slot.id) }
            return
        }
        cards.hide(animated: true)
        hoverBot = nil
        AppController.shared.cardOpen = false
        AppController.shared.cardBot = nil
    }

    func openCard(_ id: String?) {
        cardClose?.invalidate()
        hoverBot = id
        AppController.shared.cardOpen = true
        AppController.shared.cardBot = id
        if let id { bubbles.hide(id, animated: false) } else { bubbles.hideAll(animated: false) }
        showCard()
    }

    /// Closes the card the tray's click opened.
    func closeAllCard() {
        cards.hide(animated: true)
        hoverBot = nil
        AppController.shared.cardOpen = false
        AppController.shared.cardBot = nil
    }

    func refreshCard() {
        guard cards.isOpen else { return }
        showCard()
    }

    private func showCard() {
        guard let screen = screen ?? chooseScreen(), let frame else { return }
        let id = hoverBot
        let pixel = frame.isPixel
        let anchor = id.flatMap { id in slots.first { $0.id == id }?.bounds }
        let origin = panel.frame
        AppController.shared.cards(for: id) { [weak self] model in
            guard let self, let model, AppController.shared.cardOpen else { return }
            var anchorScreen = NSRect(x: origin.minX + Metrics.innerInset, y: origin.midY - Metrics.botSize / 2,
                                      width: Metrics.botSize, height: Metrics.botSize)
            if let anchor { anchorScreen = NSRect(x: origin.minX + anchor.minX, y: origin.maxY - anchor.maxY, width: anchor.width, height: anchor.height) }
            self.cards.show(model, pixel: pixel, rightEdge: frame.side == "right", anchor: anchorScreen, area: screen.visibleFrame,
                            dark: DrawerController.isDark)
        }
    }

    /// Low Power Mode, or (for the runner's saver measurement) EDGELINGS_ASSUME_BATTERY=1.
    static var onBattery: Bool {
        ProcessInfo.processInfo.isLowPowerModeEnabled || ProcessInfo.processInfo.environment["EDGELINGS_ASSUME_BATTERY"] == "1"
    }

    static var isDark: Bool {
        NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
    }

    func pressBegan(_ point: NSPoint) {
        guard let slot = slot(at: point) else { return }
        cardIntent?.invalidate()
        pressed = slot.id
        dragging = false
        refused = false
        cardHeldBack = true
        pressY = NSEvent.mouseLocation.y
        pressTop = panel.frame.maxY
    }

    private func drag() {
        let dy = pressY - NSEvent.mouseLocation.y
        guard let frame, let screen else { return }
        if refused {
            if moment?.tug == true { tug = DrawerMoves.rubber(Double(dy), threshold: Double(Metrics.dragThreshold)); view.needsDisplay = true }
            return
        }
        if !dragging && abs(dy) < Metrics.dragThreshold { return }
        if frame.locked {
            refused = true
            if let id = pressed { sayLocked(id) }
            if !reducedMotion {
                if frame.isPixel {
                    play(DrawerMoves.shake(newMoment(), topDown))
                } else {
                    let pull = DrawerMoves.rubber(Double(dy), threshold: Double(Metrics.dragThreshold))
                    tug = pull
                    let held = newMoment(holds: true)
                    held.tug = true
                    play(held)
                }
            }
            return
        }
        if !dragging {
            dragging = true
            cards.hide(animated: false)
            AppController.shared.cardOpen = false
            bubbles.hideAll(animated: false)
            // M6: the column lifts off the edge while it is dragged.
            if !reducedMotion && engine != nil { play(DrawerMoves.lift(newMoment(holds: true), topDown, from: onScreenNow)) }
        }
        let work = screen.visibleFrame
        let height = panel.frame.height
        var top = pressTop - dy
        top = min(max(top, work.minY + height), work.maxY)
        var origin = panel.frame.origin
        origin.y = top - height
        panel.setFrameOrigin(origin)
    }

    func pressEnded(_ point: NSPoint) {
        guard let id = pressed else { return }
        pressed = nil
        if dragging || refused {
            if dragging, let screen {
                let work = screen.visibleFrame
                let travel = work.height - panel.frame.height
                let fromTop = work.maxY - panel.frame.maxY
                let offset = travel > 0 ? Double(min(max(fromTop / travel, 0), 1)) : 0.5
                AppController.shared.request("setDrawer", ["offset": offset])
            }
            if !reducedMotion && engine != nil {
                if dragging {
                    play(DrawerMoves.drop(newMoment(), topDown, from: onScreenNow))
                } else if moment?.tug == true {
                    let pulled = tug
                    tug = 0
                    play(DrawerMoves.spring(newMoment(), topDown, pulled: pulled))
                }
            } else {
                moment = nil
            }
            dragging = false
            refused = false
            return
        }
        // A click: the character nods or hops, and the card gives way to Settings.
        if let index = slots.firstIndex(where: { $0.id == id }), !holdsStill(slots[index].engineState, look: slots[index].look) {
            engine?.poke(index)
            slots[index].nextStep = 0
        }
        cardClose?.invalidate()
        cards.hide(animated: frame?.isPixel != true)
        hoverBot = nil
        AppController.shared.cardOpen = false
        AppController.shared.cardBot = nil
        openSettings?(id)
    }

    func rightClick(_ point: NSPoint, event: NSEvent) {
        guard let slot = slot(at: point) else { return }
        cardClose?.invalidate()
        cardIntent?.invalidate()
        cards.hide(animated: false)
        hoverBot = nil
        AppController.shared.cardOpen = false
        AppController.shared.cardBot = nil
        showMenu?(slot.id, event.locationInWindow)
    }

    // MARK: Speech

    private func say(_ index: Int, _ bot: BotModel, _ speech: SpeechModel) {
        guard let frame, let slot = slots.indices.contains(index) ? slots[index] : nil else { return }
        let origin = panel.frame
        let anchor = NSRect(x: origin.minX + slot.bounds.minX, y: origin.maxY - slot.bounds.maxY, width: slot.bounds.width, height: slot.bounds.height)
        let area = (screen ?? chooseScreen())?.visibleFrame ?? .zero
        bubbles.say(id: bot.id, line: speech.line, kind: speech.kind, swatch: bot.swatch, pixel: frame.isPixel,
                    rightEdge: frame.side == "right", anchor: anchor, area: area, linger: speech.linger, dark: DrawerController.isDark) {
            AppController.shared.openBubbles.remove(bot.id)
            AppController.shared.markDismissed(bot.id)
        }
        AppController.shared.openBubbles.insert(bot.id)
        if let cue = speech.cue, !cue.isEmpty, !away, !(reducedMotion && cue != "ask" && cue != "alert") {
            engine?.cue(index, cue)
            slot.nextStep = 0
        }
        if speech.hop == true, !reducedMotion, !away, !holdsStill(slot.engineState, look: slot.look) {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.07) { [weak self] in
                self?.engine?.poke(index)
                self?.slots[index].nextStep = 0
            }
        }
    }

    private func sayLocked(_ id: String) {
        guard let frame, let slot = slots.first(where: { $0.id == id }) else { return }
        cards.hide(animated: false)
        let origin = panel.frame
        let anchor = NSRect(x: origin.minX + slot.bounds.minX, y: origin.maxY - slot.bounds.maxY, width: slot.bounds.width, height: slot.bounds.height)
        bubbles.say(id: id, line: "Locked in place.\nRight-click to unlock.", kind: "plain", swatch: slot.swatch, pixel: frame.isPixel,
                    rightEdge: frame.side == "right", anchor: anchor, area: (screen ?? chooseScreen())?.visibleFrame ?? .zero,
                    linger: nil, dark: DrawerController.isDark) {
            AppController.shared.openBubbles.remove(id)
            AppController.shared.markDismissed(id)
        }
        AppController.shared.openBubbles.insert(id)
    }
}

final class DrawerView: NSView {
    weak var controller: DrawerController?
    private var tracking: NSTrackingArea?

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }
    override var acceptsFirstResponder: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    /// Only the characters take clicks: the rest of the panel lets them through.
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard let controller else { return nil }
        let local = convert(point, from: superview)
        return controller.slot(at: local) == nil ? nil : self
    }

    override func mouseMoved(with event: NSEvent) { controller?.pointerMoved(convert(event.locationInWindow, from: nil)) }
    override func mouseEntered(with event: NSEvent) { controller?.pointerEntered() }
    override func mouseExited(with event: NSEvent) { controller?.pointerExited() }
    override func mouseDown(with event: NSEvent) { controller?.pressBegan(convert(event.locationInWindow, from: nil)) }
    override func mouseDragged(with event: NSEvent) { controller?.pointerMoved(convert(event.locationInWindow, from: nil)) }
    override func mouseUp(with event: NSEvent) { controller?.pressEnded(convert(event.locationInWindow, from: nil)) }
    override func rightMouseDown(with event: NSEvent) { controller?.rightClick(convert(event.locationInWindow, from: nil), event: event) }

    override func draw(_ dirtyRect: NSRect) {
        guard let controller, let ctx = NSGraphicsContext.current?.cgContext else { return }
        let scale = window?.backingScaleFactor ?? 2
        let dark = DrawerController.isDark
        for slot in controller.drawSlots {
            guard let image = controller.image(for: slot, scale: scale) else { continue }
            BotPainter.paint(ctx, image: image, bounds: slot.bounds, pose: controller.pose(of: slot),
                             pixel: controller.isPixel, edge: CGFloat(controller.edge), dark: dark, scale: scale)
        }
    }
}

/// Draws one bot in its pose, for the drawer and the motion films alike. Pixel
/// bots move in whole screen pixels with a hard shadow; Classic bots lean and
/// squash about their base over a soft floor shadow.
enum BotPainter {
    static func paint(_ ctx: CGContext, image: CGImage, bounds: CGRect, pose: BotPose, pixel: Bool, edge: CGFloat, dark: Bool, scale: CGFloat) {
        if pose.opacity <= 0 { return }
        var x = bounds.minX + CGFloat(pose.x) * edge
        var y = bounds.minY + CGFloat(pose.y)
        let size = bounds.size
        ctx.setShouldAntialias(true)
        if pixel {
            x = (x * scale).rounded() / scale
            y = (y * scale).rounded() / scale
            ctx.interpolationQuality = .none
            if pose.shadow > 0 {
                let tone = rgb(dark ? 0x0B0B0F : 0xD6CDB9)
                silhouette(ctx, image, CGRect(x: x + 2 * edge, y: y + 2, width: size.width, height: size.height), tone)
            }
            draw(ctx, image, CGRect(x: x, y: y, width: size.width, height: size.height), alpha: 1)
        } else {
            ctx.interpolationQuality = .high
            if pose.shadow > 0 { floorShadow(ctx, centre: CGPoint(x: x + size.width / 2, y: y + 41 + CGFloat(pose.shadowY)), opacity: CGFloat(pose.shadow), dark: dark) }
            ctx.saveGState()
            let base = CGPoint(x: x + size.width / 2, y: y + size.height * 0.88)
            ctx.translateBy(x: base.x, y: base.y)
            ctx.rotate(by: CGFloat(pose.lean) * edge * .pi / 180)
            ctx.scaleBy(x: CGFloat(pose.scaleX), y: CGFloat(pose.scaleY))
            ctx.translateBy(x: -base.x, y: -base.y)
            draw(ctx, image, CGRect(x: x, y: y, width: size.width, height: size.height), alpha: CGFloat(pose.opacity))
            ctx.restoreGState()
        }
    }

    /// The context is flipped (y down); CGContext.draw expects an upright image.
    private static func draw(_ ctx: CGContext, _ image: CGImage, _ rect: CGRect, alpha: CGFloat) {
        ctx.saveGState()
        ctx.setAlpha(alpha)
        ctx.translateBy(x: rect.minX, y: rect.maxY)
        ctx.scaleBy(x: 1, y: -1)
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: rect.width, height: rect.height))
        ctx.restoreGState()
    }

    /// The frame's silhouette in one colour, keeping its alpha.
    private static func silhouette(_ ctx: CGContext, _ image: CGImage, _ rect: CGRect, _ color: CGColor) {
        ctx.saveGState()
        ctx.translateBy(x: rect.minX, y: rect.maxY)
        ctx.scaleBy(x: 1, y: -1)
        let local = CGRect(x: 0, y: 0, width: rect.width, height: rect.height)
        ctx.clip(to: local, mask: image)
        ctx.setFillColor(color)
        ctx.fill(local)
        ctx.restoreGState()
    }

    /// Classic's floor shadow: a soft ellipse, 30 x 8 pt, fading out from its centre.
    private static func floorShadow(_ ctx: CGContext, centre: CGPoint, opacity: CGFloat, dark: Bool) {
        let ink: CGColor = dark ? CGColor(srgbRed: 0, green: 0, blue: 0, alpha: 0.6 * opacity) : CGColor(srgbRed: 0x17 / 255.0, green: 0x19 / 255.0, blue: 0x1F / 255.0, alpha: 0.32 * opacity)
        let clear: CGColor = ink.copy(alpha: 0) ?? ink
        guard let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: [ink, clear] as CFArray, locations: [0, 1]) else { return }
        ctx.saveGState()
        ctx.translateBy(x: centre.x, y: centre.y)
        ctx.scaleBy(x: 15, y: 4)
        ctx.drawRadialGradient(gradient, startCenter: .zero, startRadius: 0, endCenter: .zero, endRadius: 1, options: [])
        ctx.restoreGState()
    }
}
