// The drawer's character engines on JavaScriptCore: V8CharacterEngine.cs's
// host, with the same calls. The engines (characters.mjs for Pixel,
// classic.mjs for Classic) are ES modules whose only non-script syntax is a
// column-0 `export` before a const or function, so each runs as a classic
// script: its text goes into a function body without those `export`s and the
// names it exported come back as one object. The engine files stay byte for
// byte as shipped.
import Foundation
import JavaScriptCore

enum EngineError: Error, CustomStringConvertible {
    case script(String)
    var description: String {
        switch self {
        case .script(let message): return message
        }
    }
}

struct EngineFrame {
    let state: String
    let svg: String
}

final class CharacterEngine {
    let theme: String
    private let context: JSContext
    private let host: JSValue
    private let phases: Set<String>
    private let ownCues: Bool
    private var fidgetCache: [String: [String]] = [:]

    private static let hostBody = #"""
    const bots = [], makes = [];
    let errors = [];
    globalThis.@GLOBAL@ = {
        phases: () => lib.PHASES.join(','),
        fidgets: (kind, look) => lib.fidgetsOf(kind, look || undefined).join(','),
        // The cycle length of each state, in seconds: "state:seconds,…". Pixel's cycleOf takes the kind and look, Classic's the state.
        cycles: (kind, look, states) => states.split(',').map(s =>
            s + ':' + (lib.cycleOf.length > 1 ? lib.cycleOf(kind, s, look || undefined) : lib.cycleOf(s))).join(','),
        add(kind, seed, tempo, look) {
            const make = { kind, seed, tempo };
            makes.push(make);
            bots.push(lib.createBot(kind, { seed, tempo, rim: true, look: look || undefined }));
            return bots.length - 1;
        },
        setLook(i, look) {
            const { kind, seed, tempo } = makes[i], old = bots[i];
            bots[i] = lib.createBot(kind, { seed, tempo, rim: true, look: look || undefined });
            bots[i].setState(old.state, -1);
        },
        setState(i, state, phase) { bots[i].setState(state, phase); },
        setGaze(i, dx, dy) { bots[i].setGaze(dx, dy); },
        clearGaze(i) { bots[i].clearGaze(); },
        poke(i) { bots[i].poke(); },
        cue(i, kind) { bots[i].cue?.(kind); },
        // The bench's split of one tick: the advance alone, and the SVG alone.
        advanceOnly(steps) {
            const dts = steps.split(',');
            for (let i = 0; i < bots.length; i++) if (dts[i]) bots[i].advance(+dts[i]);
            return '';
        },
        svgOnly(size) {
            let total = 0;
            for (let i = 0; i < bots.length; i++) total += bots[i].svg(size, 'b' + i).length;
            return total;
        },
        tick(steps, size) {
            const dts = steps.split(','), parts = [];
            for (let i = 0; i < bots.length; i++) {
                if (!dts[i]) { parts.push(''); continue; }
                // One character's exception costs it this frame only: it keeps its last good picture.
                try {
                    bots[i].advance(+dts[i]);
                    parts.push(bots[i].state + '\u0002' + bots[i].svg(size, 'b' + i));
                } catch (error) {
                    errors.push(makes[i].kind + ': ' + (error && error.message ? error.message : String(error)));
                    parts.push('');
                }
            }
            const failed = errors.join('; ');
            errors = [];
            return parts.join('\u0001') + (failed ? '\u0003' + failed : '');
        },
    };
    """#

    /// theme is "pixel" or "classic"; directory holds characters.mjs and classic.mjs.
    init(theme: String, directory: String) throws {
        self.theme = theme
        let file = theme == "pixel" ? "characters.mjs" : "classic.mjs"
        let global = theme == "pixel" ? "agentIslandPixels" : "agentIslandClassic"
        ownCues = theme != "pixel"
        var source = try String(contentsOfFile: directory + "/" + file, encoding: .utf8)
        let exports = try NSRegularExpression(pattern: #"^export (?:const|function) (\w+)"#, options: [.anchorsMatchLines])
        var names: [String] = []
        for match in exports.matches(in: source, range: NSRange(source.startIndex..., in: source)) {
            names.append((source as NSString).substring(with: match.range(at: 1)))
        }
        source = source.replacingOccurrences(of: #"(?m)^export "#, with: "", options: .regularExpression)
        let script = "const lib = (function () {\n'use strict';\n" + source + "\nreturn { " + names.joined(separator: ", ") + " };\n})();\n"
            + CharacterEngine.hostBody.replacingOccurrences(of: "@GLOBAL@", with: global)
        guard let context = JSContext() else { throw EngineError.script("no JSContext") }
        self.context = context
        var message: String?
        context.exceptionHandler = { _, exception in message = exception?.toString() }
        context.evaluateScript(script, withSourceURL: URL(string: "file:///" + file))
        if let message { throw EngineError.script("\(file): \(message)") }
        guard let host = context.objectForKeyedSubscript(global), !host.isUndefined else {
            throw EngineError.script("\(file) defined no \(global)")
        }
        self.host = host
        let list = host.invokeMethod("phases", withArguments: [])?.toString() ?? ""
        phases = Set(list.split(separator: ",").map(String.init))
    }

    @discardableResult
    private func call(_ name: String, _ arguments: [Any]) -> JSValue? {
        var message: String?
        context.exceptionHandler = { _, exception in message = exception?.toString() }
        let result = host.invokeMethod(name, withArguments: arguments)
        if let message { NSLog("engine \(theme) \(name): \(message)") }
        return result
    }

    /// kind is the character's DrawerLooks.Kind; look is a LookCatalog id or "".
    func addBot(kind: String, tempo: Double, seed: Int, look: String) -> Int {
        Int(call("add", [kind, seed, tempo, look])?.toInt32() ?? -1)
    }

    func setLook(_ bot: Int, _ look: String) { call("setLook", [bot, look]) }

    func setState(_ bot: Int, _ state: String, phase: Double) { call("setState", [bot, state, phase]) }

    /// The bot looks along the pointer's offset from its centre, in the drawer's points.
    func setGaze(_ bot: Int, dx: Double, dy: Double) { call("setGaze", [bot, dx, dy]) }

    func clearGaze(_ bot: Int) { call("clearGaze", [bot]) }

    func poke(_ bot: Int) { call("poke", [bot]) }

    /// A one-shot cue where the set has its own (Classic); Pixel's answer is its poke hop.
    func cue(_ bot: Int, _ kind: String) {
        if ownCues { call("cue", [bot, kind]) } else { poke(bot) }
    }

    /// Each state's cycle length in seconds on a bot with this tempo, for the work-state rotation.
    func cycles(kind: String, look: String, states: [String], tempo: Double) -> [String: Double] {
        let raw = call("cycles", [kind, look, states.joined(separator: ",")])?.toString() ?? ""
        var result: [String: Double] = [:]
        for pair in raw.split(separator: ",") {
            let parts = pair.split(separator: ":")
            if parts.count == 2, let seconds = Double(parts[1]) { result[String(parts[0])] = seconds / max(tempo, 0.01) }
        }
        return result
    }

    /// Called once per launch with the first exception a character's step threw (the drawer logs it).
    static var onError: ((String) -> Void)?
    private static var reported = false

    func fidgets(kind: String, look: String) -> [String] {
        let key = kind + "/" + look
        if let known = fidgetCache[key] { return known }
        let list = call("fidgets", [kind, look])?.toString() ?? ""
        let parsed = list.split(separator: ",").map(String.init)
        fidgetCache[key] = parsed
        return parsed
    }

    func draws(_ state: String) -> Bool { phases.contains(state) }

    /// Advances each bot whose dt is a number (nil skips it) and returns its
    /// (engine state, SVG); skipped bots come back nil.
    func tick(_ dts: [Double?], size: Double) -> [EngineFrame?] {
        let steps = dts.map { $0.map { String($0) } ?? "" }.joined(separator: ",")
        var raw = call("tick", [steps, size])?.toString() ?? ""
        if let marker = raw.range(of: "\u{3}") {
            if !CharacterEngine.reported {
                CharacterEngine.reported = true
                CharacterEngine.onError?("engine \(theme) step threw: " + String(String(raw[marker.upperBound...]).prefix(150)))
            }
            raw = String(raw[raw.startIndex..<marker.lowerBound])
        }
        return raw.components(separatedBy: "\u{1}").map { part in
            guard let split = part.range(of: "\u{2}") else { return nil }
            return EngineFrame(state: String(part[part.startIndex..<split.lowerBound]), svg: String(part[split.upperBound...]))
        }
    }

    /// For `--bench-loop`: the host calls that split one tick into its parts.
    func benchAdvance(_ dts: [Double?]) {
        call("advanceOnly", [dts.map { $0.map { String($0) } ?? "" }.joined(separator: ",")])
    }

    func benchSvg(size: Double) { call("svgOnly", [size]) }

    /// The tick's own call with the Swift side timed apart: (JS call and string handover, splitting), in ms.
    func benchTick(_ dts: [Double?], size: Double) -> (call: Double, split: Double) {
        let steps = dts.map { $0.map { String($0) } ?? "" }.joined(separator: ",")
        let start = DispatchTime.now().uptimeNanoseconds
        let raw = call("tick", [steps, size])?.toString() ?? ""
        let middle = DispatchTime.now().uptimeNanoseconds
        _ = raw.components(separatedBy: "\u{1}").map { part in part.range(of: "\u{2}") }
        let end = DispatchTime.now().uptimeNanoseconds
        return (Double(middle - start) / 1e6, Double(end - middle) / 1e6)
    }
}
