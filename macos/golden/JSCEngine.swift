// The character engines in JavaScriptCore. The engines are ES modules whose
// only syntax outside plain script is a column-0 `export` in front of a const
// or a function, so a module is run as a classic script: its text goes into a
// function body without those `export`s, and the names it exported come back
// as one object. The engine files themselves stay byte for byte as shipped.
// The host below is V8CharacterEngine.cs's, minus its import line.
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

final class JSCEngine {
    private let context: JSContext
    private let host: JSValue
    private var failure: String?

    private static let hostBody = #"""
    const bots = [], makes = [];
    globalThis.@GLOBAL@ = {
        phases: () => lib.PHASES.join(','),
        fidgets: (kind, look) => lib.fidgetsOf(kind, look || undefined).join(','),
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
        tick(steps, size) {
            const dts = steps.split(','), parts = [];
            for (let i = 0; i < bots.length; i++) {
                if (!dts[i]) { parts.push(''); continue; }
                bots[i].advance(+dts[i]);
                parts.push(bots[i].state + '\u0002' + bots[i].svg(size, 'b' + i));
            }
            return parts.join('\u0001');
        },
    };
    """#

    /// theme is "pixel" or "classic"; directory holds characters.mjs and classic.mjs.
    init(theme: String, directory: String) throws {
        let file = theme == "pixel" ? "characters.mjs" : "classic.mjs"
        let global = theme == "pixel" ? "agentIslandPixels" : "agentIslandClassic"
        var source = try String(contentsOfFile: directory + "/" + file, encoding: .utf8)
        let exports = try NSRegularExpression(pattern: #"^export (?:const|function) (\w+)"#, options: [.anchorsMatchLines])
        var names: [String] = []
        for match in exports.matches(in: source, range: NSRange(source.startIndex..., in: source)) {
            names.append((source as NSString).substring(with: match.range(at: 1)))
        }
        source = source.replacingOccurrences(of: #"(?m)^export "#, with: "", options: .regularExpression)
        let script = "const lib = (function () {\n'use strict';\n" + source + "\nreturn { " + names.joined(separator: ", ") + " };\n})();\n"
            + JSCEngine.hostBody.replacingOccurrences(of: "@GLOBAL@", with: global)
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
    }

    private func call(_ name: String, _ arguments: [Any]) throws -> JSValue {
        var message: String?
        context.exceptionHandler = { _, exception in message = exception?.toString() }
        let result = host.invokeMethod(name, withArguments: arguments)
        if let message { throw EngineError.script("\(name): \(message)") }
        return result ?? JSValue(undefinedIn: context)
    }

    func phases() throws -> [String] {
        try call("phases", []).toString().split(separator: ",").map(String.init)
    }

    func add(kind: String, seed: Int, tempo: Double, look: String) throws -> Int {
        Int(try call("add", [kind, seed, tempo, look]).toInt32())
    }

    func setState(_ bot: Int, _ state: String, phase: Double) throws {
        _ = try call("setState", [bot, state, phase])
    }

    /// Advances one bot by dt and returns its SVG; the others stay as they are.
    func tick(bot: Int, dt: Double, size: Double) throws -> String {
        let steps = String(repeating: ",", count: bot) + String(dt)
        let raw = try call("tick", [steps, size]).toString() ?? ""
        let parts = raw.components(separatedBy: "\u{1}")
        guard bot < parts.count, let split = parts[bot].range(of: "\u{2}") else {
            throw EngineError.script("tick returned nothing for bot \(bot)")
        }
        return String(parts[bot][split.upperBound...])
    }
}
