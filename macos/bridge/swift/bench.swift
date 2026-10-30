// Stage 0: calls the bridge both ways and prints what it costs.
//   bench a1 <libAgentIslandBridge.dylib> <kimiHome>     the NativeAOT dylib, in process
//   bench a2 <AgentIslandHelper> <kimiHome>              the helper process, JSON lines
import Foundation
import Darwin

func nowMs() -> Double {
    var t = timespec()
    clock_gettime(CLOCK_MONOTONIC_RAW, &t)
    return Double(t.tv_sec) * 1000 + Double(t.tv_nsec) / 1_000_000
}

/// Resident memory of this process, in MB.
func residentMB() -> Double {
    var info = mach_task_basic_info()
    var count = mach_msg_type_number_t(MemoryLayout<mach_task_basic_info>.size / MemoryLayout<natural_t>.size)
    let result = withUnsafeMutablePointer(to: &info) {
        $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
            task_info(mach_task_self_, task_flavor_t(MACH_TASK_BASIC_INFO), $0, &count)
        }
    }
    return result == KERN_SUCCESS ? Double(info.resident_size) / 1_048_576 : -1
}

func f(_ value: Double) -> String { String(format: "%.1f", value) }

let args = CommandLine.arguments
guard args.count >= 4 else {
    print("usage: bench a1|a2 <path> <kimiHome>")
    exit(2)
}
let mode = args[1], path = args[2], home = args[3]
let versionRequest = "{\"method\":\"version\"}"
// The bench keeps Core's whole world (settings, parse cache, agent folders)
// under a temp root, so it never rewrites the real ones.
let benchRoot = NSTemporaryDirectory() + "edgelings-bench-" + UUID().uuidString
let configureRequest = "{\"method\":\"configure\",\"root\":\"\(benchRoot)\"}"
let scanRequest = "{\"method\":\"scan\",\"kimiHome\":\"\(home)\",\"lookbackDays\":60}"

if mode == "a1" {
    typealias CallFn = @convention(c) (UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>?
    typealias FreeFn = @convention(c) (UnsafeMutablePointer<CChar>?) -> Void
    let before = residentMB()
    let start = nowMs()
    guard let handle = dlopen(path, RTLD_NOW) else {
        print("A1 dlopen failed: \(String(cString: dlerror()))")
        exit(1)
    }
    guard let callSymbol = dlsym(handle, "ai_call"), let freeSymbol = dlsym(handle, "ai_free") else {
        print("A1 symbols missing")
        exit(1)
    }
    let call = unsafeBitCast(callSymbol, to: CallFn.self)
    let release = unsafeBitCast(freeSymbol, to: FreeFn.self)
    func ask(_ request: String) -> String {
        guard let reply = call(request) else { return "(nil)" }
        defer { release(reply) }
        return String(cString: reply)
    }
    let version = ask(versionRequest)
    _ = ask(configureRequest)
    let cold = nowMs() - start
    let afterStart = residentMB()
    let scanStart = nowMs()
    let scan = ask(scanRequest)
    let firstScan = nowMs() - scanStart
    let warmStart = nowMs()
    _ = ask(scanRequest)
    let warmScan = nowMs() - warmStart
    let afterScan = residentMB()
    print("A1 cold start (dlopen + first call): \(f(cold)) ms")
    print("A1 first scan: \(f(firstScan)) ms, second scan (parse cache warm): \(f(warmScan)) ms")
    print("A1 resident: \(f(before)) MB before, \(f(afterStart)) MB after start, \(f(afterScan)) MB after scans")
    print("A1 version: \(version)")
    print("A1 scan: \(scan)")
} else if mode == "a2" {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: path)
    let input = Pipe(), output = Pipe()
    process.standardInput = input
    process.standardOutput = output
    process.standardError = FileHandle.standardError
    func ask(_ request: String) -> String {
        input.fileHandleForWriting.write((request + "\n").data(using: .utf8)!)
        var buffer = Data()
        while true {
            let chunk = output.fileHandleForReading.availableData
            if chunk.isEmpty { break }
            buffer.append(chunk)
            if buffer.last == 10 { break }
        }
        return String(data: buffer, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }
    func helperResidentMB() -> Double {
        let ps = Process()
        ps.executableURL = URL(fileURLWithPath: "/bin/ps")
        ps.arguments = ["-o", "rss=", "-p", String(process.processIdentifier)]
        let pipe = Pipe()
        ps.standardOutput = pipe
        try? ps.run()
        ps.waitUntilExit()
        let text = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        return (Double(text.trimmingCharacters(in: .whitespacesAndNewlines)) ?? 0) / 1024
    }
    let start = nowMs()
    do { try process.run() } catch {
        print("A2 launch failed: \(error)")
        exit(1)
    }
    let version = ask(versionRequest)
    _ = ask(configureRequest)
    let cold = nowMs() - start
    let afterStart = helperResidentMB()
    let scanStart = nowMs()
    let scan = ask(scanRequest)
    let firstScan = nowMs() - scanStart
    let warmStart = nowMs()
    _ = ask(scanRequest)
    let warmScan = nowMs() - warmStart
    let afterScan = helperResidentMB()
    print("A2 cold start (launch + first reply): \(f(cold)) ms")
    print("A2 first scan: \(f(firstScan)) ms, second scan (parse cache warm): \(f(warmScan)) ms")
    print("A2 helper resident: \(f(afterStart)) MB after start, \(f(afterScan)) MB after scans")
    print("A2 version: \(version)")
    print("A2 scan: \(scan)")
    input.fileHandleForWriting.closeFile()
    process.waitUntilExit()
} else {
    print("unknown mode \(mode)")
    exit(2)
}
