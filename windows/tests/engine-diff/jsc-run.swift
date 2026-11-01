// Runs the Pixel engine differential and per-look timing on JavaScriptCore (the Mac app's
// script engine), which prices the same code differently from V8.
//
//   swiftc -O jsc-run.swift -o jsc-run
//   jsc-run <frozen engine.mjs> <live engine.mjs> <diff-core.mjs> [steps] [seeds]
import Foundation
import JavaScriptCore

let arguments = CommandLine.arguments
guard arguments.count >= 4 || arguments.contains("--micro") || arguments.contains("--profile") else {
    print("usage: jsc-run <frozen.mjs> <live.mjs> <diff-core.mjs> [steps] [seeds]")
    exit(2)
}
// jsc-run --micro <script.js>: runs a plain script with print() and preciseTime() and nothing else.
if arguments.count >= 3, arguments[1] == "--micro" {
    let context = JSContext()!
    context.exceptionHandler = { _, exception in print("FAIL \(exception?.toString() ?? "")") }
    let printer: @convention(block) (String) -> Void = { print($0) }
    context.setObject(printer, forKeyedSubscript: "print" as NSString)
    let clock: @convention(block) () -> Double = { ProcessInfo.processInfo.systemUptime }
    context.setObject(clock, forKeyedSubscript: "preciseTime" as NSString)
    context.evaluateScript(try! String(contentsOfFile: arguments[2], encoding: .utf8))
    exit(0)
}
// jsc-run --profile <live.mjs> <profile.js>: where a frame's time goes inside the engine, by function
// (inclusive, so nested ones overlap). profile.js runs with the engine bound to B.
if arguments.count >= 4, arguments[1] == "--profile" {
    var source = try! String(contentsOfFile: arguments[2], encoding: .utf8)
    let names = ["pixels", "ringOf", "runsOf", "fitDraw", "settle", "fitShift", "allSafe"]
    var wrappers = ""
    for name in names {
        source = source.replacingOccurrences(of: "function " + name + "(", with: "function " + name + "__orig(")
        source = source.replacingOccurrences(of: "const " + name + " = ", with: "const " + name + "__orig = ")
        wrappers += "const " + name + " = (...a) => { const t = preciseTime(); try { return " + name + "__orig(...a); } finally { PROF." + name + " += preciseTime() - t; } };" + String(UnicodeScalar(10))
    }
    source = source.replacingOccurrences(of: "const body = def.draw(shown, c);", with: "const __t0 = preciseTime(); const body = def.draw(shown, c); PROF.draw += preciseTime() - __t0;")
    let exports = try! NSRegularExpression(pattern: #"^export (?:const|function) (\w+)"#, options: [.anchorsMatchLines])
    var exported: [String] = []
    for match in exports.matches(in: source, range: NSRange(source.startIndex..., in: source)) {
        exported.append((source as NSString).substring(with: match.range(at: 1)))
    }
    source = source.replacingOccurrences(of: #"(?m)^export "#, with: "", options: .regularExpression)
    let nl = String(UnicodeScalar(10))
    let script = "const PROF = { draw: 0, " + names.map { $0 + ": 0" }.joined(separator: ", ") + " };" + nl
        + "const B = (function () {" + nl + "'use strict';" + nl + source + nl + wrappers + "return { " + exported.joined(separator: ", ") + " };" + nl + "})();" + nl
        + (try! String(contentsOfFile: arguments[3], encoding: .utf8))
    let context = JSContext()!
    context.exceptionHandler = { _, exception in print("FAIL " + (exception?.toString() ?? "")) }
    let printer: @convention(block) (String) -> Void = { print($0) }
    context.setObject(printer, forKeyedSubscript: "print" as NSString)
    let clock: @convention(block) () -> Double = { ProcessInfo.processInfo.systemUptime }
    context.setObject(clock, forKeyedSubscript: "preciseTime" as NSString)
    context.evaluateScript(script)
    exit(0)
}
let steps = arguments.count > 4 ? Int(arguments[4]) ?? 500 : 500
let seeds = arguments.count > 5 ? Int(arguments[5]) ?? 2 : 2

/// An engine file as an expression: its text in a function body without the column-0 `export`s, returning its exports.
func wrapped(_ path: String) throws -> String {
    var source = try String(contentsOfFile: path, encoding: .utf8)
    let exports = try NSRegularExpression(pattern: #"^export (?:const|function) (\w+)"#, options: [.anchorsMatchLines])
    var names: [String] = []
    for match in exports.matches(in: source, range: NSRange(source.startIndex..., in: source)) {
        names.append((source as NSString).substring(with: match.range(at: 1)))
    }
    source = source.replacingOccurrences(of: #"(?m)^export "#, with: "", options: .regularExpression)
    return "(function () {\n'use strict';\n" + source + "\nreturn { " + names.joined(separator: ", ") + " };\n})()"
}

let glue = #"""
const states = ['idle', 'thinking', 'writing', 'running', 'sleeping'];
const rounds = 150;
function time(lib, kind, look, state) {
  const bot = lib.createBot(kind, { seed: 7, tempo: 1, rim: true, look });
  bot.setState(state, 0);
  for (let i = 0; i < 40; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
  const start = preciseTime();
  for (let i = 0; i < rounds; i++) { bot.advance(1 / 30); bot.svg(44, 'b0'); }
  return (preciseTime() - start) * 1000 / rounds;
}
let oldTotal = 0, newTotal = 0, count = 0;
for (const kind of A.KINDS) {
  if (kind === 'cursor') continue;
  for (const { id } of A.LOOKS[kind]) {
    let o = 0, n = 0;
    for (const state of states) { o += time(A, kind, id, state); n += time(B, kind, id, state); }
    o /= states.length; n /= states.length;
    oldTotal += o; newTotal += n; count++;
    print((kind + '/' + id).padEnd(24) + ' old ' + o.toFixed(3) + ' ms  new ' + n.toFixed(3) + ' ms  x' + (o / n).toFixed(2));
  }
}
print('mean per advance+svg under JavaScriptCore: old ' + (oldTotal / count).toFixed(3) + ' ms, new ' + (newTotal / count).toFixed(3) + ' ms, x' + (oldTotal / newTotal).toFixed(2));
const result = runDiff(A, B, STEPS, SEEDS);
print('engine differential under JavaScriptCore: ' + result.runs + ' runs, ' + result.checked + ' SVG comparisons, all identical');
"""#

do {
    let core = try String(contentsOfFile: arguments[3], encoding: .utf8).replacingOccurrences(of: "export function", with: "function")
    let script = "const A = \(try wrapped(arguments[1]));\nconst B = \(try wrapped(arguments[2]));\n" + core + "\n"
        + glue.replacingOccurrences(of: "STEPS", with: String(steps)).replacingOccurrences(of: "SEEDS", with: String(seeds))
    let context = JSContext()!
    var failure: String?
    context.exceptionHandler = { _, exception in failure = exception?.toString() }
    let printer: @convention(block) (String) -> Void = { print($0) }
    context.setObject(printer, forKeyedSubscript: "print" as NSString)
    let clock: @convention(block) () -> Double = { ProcessInfo.processInfo.systemUptime }
    context.setObject(clock, forKeyedSubscript: "preciseTime" as NSString)
    context.evaluateScript(script)
    if let failure {
        print("FAIL \(failure)")
        exit(1)
    }
} catch {
    print("FAIL \(error)")
    exit(1)
}
