// The motion system both styles share (Motion.cs, DrawerMoment.cs): Classic
// eases on cubic Béziers, Pixel steps whole 2-pt cells on a 30-tick clock. A
// track gives one value at a time since its moment started. The drawer's
// moments (M1 reveal and hide, M6 lift, drop and refusal, M20 flip) are
// built from DrawerMoves, whose numbers are the Windows ones.
import Foundation

enum Motion {
    /// Pixel's clock: 30 ticks a second.
    static let tickMs: Double = 1000.0 / 30

    /// Frame k of a pixel phase shows k ticks after it starts.
    static func tick(_ ms: Double) -> Int { Int((ms / tickMs + 1e-6).rounded(.down)) }

    /// Whole 2-pt cells; halves round up, as in the design's prototypes.
    static func cell(_ dip: Double) -> Double { (dip / 2 + 0.5).rounded(.down) * 2 }

    typealias Curve = (Double) -> Double

    static let out = bezier(0.22, 1, 0.36, 1)
    static let `in` = bezier(0.4, 0, 1, 1)
    static let pop = bezier(0.34, 1.56, 0.64, 1)
    static let softPop = bezier(0.3, 1.3, 0.6, 1)
    static let linear: Curve = { min(max($0, 0), 1) }

    /// A cubic Bézier easing solved the way CSS solves it: Newton's method on x, then y.
    static func bezier(_ x1: Double, _ y1: Double, _ x2: Double, _ y2: Double) -> Curve {
        let cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx
        let cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by
        func xAt(_ t: Double) -> Double { ((ax * t + bx) * t + cx) * t }
        func yAt(_ t: Double) -> Double { ((ay * t + by) * t + cy) * t }
        func slope(_ t: Double) -> Double { (3 * ax * t + 2 * bx) * t + cx }
        return { x in
            if x <= 0 { return 0 }
            if x >= 1 { return 1 }
            var t = x
            for _ in 0..<8 {
                let error = xAt(t) - x
                let s = slope(t)
                if abs(error) < 1e-5 || abs(s) < 1e-6 { break }
                t -= error / s
            }
            return yAt(min(max(t, 0), 1))
        }
    }

    static let in2: [Double] = [0.35, 0]
    static let in3: [Double] = [0.5, 0.15, 0]

    /// Pixel's remaining-distance curves: the share of the way still to go
    /// after each frame, the last always exactly there.
    static func remaining(_ from: Double, _ to: Double, _ left: [Double]) -> [Double] {
        left.enumerated().map { index, share in index == left.count - 1 ? to : to - cell((to - from) * share) }
    }
}

protocol Track {
    func value(_ ms: Double) -> Double
    var endMs: Double { get }
}

/// Classic: from, eased to to over a duration, after a delay.
struct Tween: Track {
    let from: Double
    let to: Double
    var delay: Double = 0
    let duration: Double
    var curve: Motion.Curve = Motion.linear

    var endMs: Double { delay + duration }

    func value(_ ms: Double) -> Double {
        if ms <= delay { return from }
        let share = duration <= 0 ? 1 : (ms - delay) / duration
        return share >= 1 ? to : from + (to - from) * curve(share)
    }
}

/// Classic: several tweens on one value, one after another.
struct Keys: Track {
    let legs: [Tween]
    var endMs: Double { legs.last?.endMs ?? 0 }

    func value(_ ms: Double) -> Double {
        for leg in legs where ms < leg.endMs { return leg.value(ms) }
        return legs.last?.value(ms) ?? 0
    }
}

/// Pixel: frame values held on whole ticks. Before the phase starts it shows
/// `before`; frame k (from 1) lands on tick startTick + k.
struct Steps: Track {
    let before: Double
    var startTick = 0
    let frames: [Double]

    var endMs: Double { Double(startTick + frames.count) * Motion.tickMs }

    func value(_ ms: Double) -> Double {
        let k = Motion.tick(ms) - startTick
        if k <= 0 || frames.isEmpty { return before }
        return frames[min(k, frames.count) - 1]
    }
}

enum BotProp: Hashable {
    case x, y, lean, scaleX, scaleY, opacity, shadow, shadowY
}

/// One bot as a drawer moment draws it.
struct BotPose {
    var x = 0.0
    var y = 0.0
    var lean = 0.0
    var scaleX = 1.0
    var scaleY = 1.0
    var opacity = 1.0
    var shadow = 0.0
    var shadowY = 0.0
    static let rest = BotPose()
}

private struct TrackKey: Hashable {
    let index: Int
    let prop: BotProp
}

/// A drawer moment as the game loop plays it: the bots it moves and the tracks
/// that move them from its start.
final class DrawerMoment {
    let start: Double
    let pixel: Bool
    let edge: Double
    /// A held moment keeps its last frame until another replaces it.
    var holds = false
    /// Classic's locked-drag tug: the live pull is added to every bot's y.
    var tug = false
    var ended = false
    private(set) var duration = 0.0
    private var tracks: [TrackKey: Track] = [:]
    private var moved = Set<Int>()
    private var actions: [(ms: Double, run: () -> Void)] = []
    private var ran = 0

    init(start: Double, pixel: Bool, edge: Double) {
        self.start = start
        self.pixel = pixel
        self.edge = edge
    }

    @discardableResult
    func set(_ index: Int, _ prop: BotProp, _ track: Track) -> DrawerMoment {
        tracks[TrackKey(index: index, prop: prop)] = track
        moved.insert(index)
        duration = max(duration, track.endMs)
        return self
    }

    /// Something for the loop to do once the moment reaches ms (M20 swaps the look edge-on).
    @discardableResult
    func at(_ ms: Double, _ action: @escaping () -> Void) -> DrawerMoment {
        actions.append((ms, action))
        actions.sort { $0.ms < $1.ms }
        duration = max(duration, ms)
        return self
    }

    func moving(_ ms: Double) -> Bool { ms < duration }

    func runDue(_ ms: Double) {
        while ran < actions.count, actions[ran].ms <= ms {
            actions[ran].run()
            ran += 1
        }
    }

    func runRest() {
        while ran < actions.count {
            actions[ran].run()
            ran += 1
        }
    }

    func pose(_ index: Int, _ ms: Double) -> BotPose {
        func get(_ prop: BotProp, _ rest: Double) -> Double { tracks[TrackKey(index: index, prop: prop)]?.value(ms) ?? rest }
        return BotPose(x: get(.x, 0), y: get(.y, 0), lean: get(.lean, 0), scaleX: get(.scaleX, 1), scaleY: get(.scaleY, 1),
                       opacity: get(.opacity, 1), shadow: get(.shadow, 0), shadowY: get(.shadowY, 0))
    }

    /// A pose lookup for a moment that takes over: it starts from the frame on screen.
    func onScreen(at ms: Double) -> (Int) -> BotPose? {
        { [self] index in moved.contains(index) ? pose(index, ms) : nil }
    }
}

enum DrawerMoves {
    typealias OnScreen = (Int) -> BotPose?

    /// Far enough out that a bot is off the screen edge.
    static let off = 50.0

    /// M1 reveal: each bot slides in from beyond the screen edge, Pixel in
    /// pairs from the top a tick apart, Classic 33 ms apart with a lean that
    /// rocks back.
    static func reveal(_ moment: DrawerMoment, _ topDown: [Int], staggered: Bool = true, delay: Double = 0, from: OnScreen? = nil) -> DrawerMoment {
        for (i, index) in topDown.enumerated() {
            let now = from?(index)
            let x = now?.x ?? off
            if moment.pixel {
                let start = Motion.tick(delay) + (staggered ? i / 2 : 0)
                moment.set(index, .x, Steps(before: x, startTick: start, frames: Motion.remaining(x, 0, Motion.in3)))
            } else {
                let wait = delay + (staggered ? Double(i) * 33 : 0)
                moment.set(index, .x, Tween(from: x, to: 0, delay: wait, duration: 333, curve: Motion.pop))
                moment.set(index, .lean, Tween(from: now?.lean ?? -10, to: 0, delay: wait, duration: 333, curve: Motion.pop))
                moment.set(index, .opacity, Tween(from: now?.opacity ?? 0, to: 1, delay: wait, duration: 83, curve: Motion.linear))
            }
        }
        return moment
    }

    /// M1 hide: every bot leaves toward the edge. Pixel all at once in two
    /// ticks (30 % of the way, then gone); Classic bottom to top, 17 ms apart, fading.
    static func hide(_ moment: DrawerMoment, _ topDown: [Int], staggered: Bool = true, from: OnScreen? = nil) -> DrawerMoment {
        for (i, index) in topDown.enumerated() {
            let now = from?(index) ?? .rest
            if moment.pixel {
                moment.set(index, .x, Steps(before: now.x, startTick: 0, frames: [now.x + Motion.cell((off - now.x) * 0.3), off]))
            } else {
                let wait = staggered ? Double(topDown.count - 1 - i) * 17 : 0
                moment.set(index, .x, Tween(from: now.x, to: off, delay: wait, duration: 167, curve: Motion.in))
                moment.set(index, .opacity, Tween(from: now.opacity, to: 0, delay: wait + 84, duration: 83, curve: Motion.linear))
            }
        }
        return moment
    }

    /// M6 lift: Pixel steps the bots 2, then 4 pt off the edge with a hard
    /// shadow; Classic swells them over a floor shadow.
    static func lift(_ moment: DrawerMoment, _ bots: [Int], from: OnScreen? = nil) -> DrawerMoment {
        for index in bots {
            let now = from?(index) ?? .rest
            if moment.pixel {
                moment.set(index, .x, Steps(before: now.x, startTick: 0, frames: Motion.remaining(now.x, -4, Motion.in2)))
                moment.set(index, .shadow, Steps(before: now.shadow, startTick: 0, frames: [1, 1]))
            } else {
                moment.set(index, .scaleX, Tween(from: now.scaleX, to: 1.07, duration: 167, curve: Motion.out))
                moment.set(index, .scaleY, Tween(from: now.scaleY, to: 1.07, duration: 167, curve: Motion.out))
                moment.set(index, .shadow, Tween(from: now.shadow, to: 1, duration: 167, curve: Motion.out))
                moment.set(index, .shadowY, Tween(from: now.shadowY, to: 3, duration: 167, curve: Motion.out))
            }
        }
        return moment
    }

    /// M6 drop: Pixel steps back with a 2-pt thud; Classic lands with a squash.
    static func drop(_ moment: DrawerMoment, _ bots: [Int], from: OnScreen? = nil) -> DrawerMoment {
        for index in bots {
            let now = from?(index) ?? .rest
            if moment.pixel {
                moment.set(index, .x, Steps(before: now.x, startTick: 0, frames: Motion.remaining(now.x, 0, Motion.in2) + [0]))
                moment.set(index, .y, Steps(before: 0, startTick: 0, frames: [0, 2, 0]))
                moment.set(index, .shadow, Steps(before: now.shadow, startTick: 0, frames: [now.shadow, now.shadow, 0]))
            } else {
                moment.set(index, .scaleX, Keys(legs: [Tween(from: now.scaleX, to: 1.05, duration: 83, curve: Motion.out),
                                                       Tween(from: 1.05, to: 1, delay: 83, duration: 250, curve: Motion.pop)]))
                moment.set(index, .scaleY, Keys(legs: [Tween(from: now.scaleY, to: 0.93, duration: 83, curve: Motion.out),
                                                       Tween(from: 0.93, to: 1, delay: 83, duration: 250, curve: Motion.pop)]))
                moment.set(index, .shadow, Tween(from: now.shadow, to: 0, duration: 167))
                moment.set(index, .shadowY, Tween(from: now.shadowY, to: 0, duration: 167))
            }
        }
        return moment
    }

    /// M6 refusal, Pixel: the column shakes across, +2, -2, +2 and home.
    static func shake(_ moment: DrawerMoment, _ bots: [Int]) -> DrawerMoment {
        for index in bots { moment.set(index, .x, Steps(before: 0, startTick: 0, frames: [2, -2, 2, 0])) }
        return moment
    }

    /// M6 refusal, Classic: the rubber-banded column springs home.
    static func spring(_ moment: DrawerMoment, _ bots: [Int], pulled: Double) -> DrawerMoment {
        for index in bots { moment.set(index, .y, Tween(from: pulled, to: 0, duration: 333, curve: Motion.pop)) }
        return moment
    }

    /// M20, Classic's look change: the bot turns edge-on like a coin in 120 ms,
    /// its new look swaps in there, and it turns back with a 3-pt hop in 200
    /// ms. Reduced motion fades it out and in over 120 ms each instead.
    static func flip(_ moment: DrawerMoment, _ index: Int, reduced: Bool, swap: @escaping () -> Void) -> DrawerMoment {
        if reduced {
            moment.set(index, .opacity, Keys(legs: [Tween(from: 1, to: 0, duration: 120), Tween(from: 0, to: 1, delay: 120, duration: 120)]))
            return moment.at(120, swap)
        }
        moment.set(index, .scaleX, Keys(legs: [Tween(from: 1, to: 0, duration: 120, curve: Motion.in),
                                               Tween(from: 0, to: 1.06, delay: 120, duration: 120, curve: Motion.out),
                                               Tween(from: 1.06, to: 1, delay: 240, duration: 80, curve: Motion.out)]))
        moment.set(index, .y, Keys(legs: [Tween(from: 0, to: -3, delay: 120, duration: 100, curve: Motion.out),
                                          Tween(from: -3, to: 0, delay: 220, duration: 100, curve: Motion.in)]))
        return moment.at(120, swap)
    }

    /// Classic's rubber band: a fifth of the pull past the threshold, at most 8 pt.
    static func rubber(_ pull: Double, threshold: Double) -> Double {
        (pull < 0 ? -1 : 1) * min(8, max(0, abs(pull) - threshold) / 5)
    }
}
