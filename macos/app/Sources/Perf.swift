// Where the app's own time goes, by subsystem, for the diagnostic: the wall time of each named piece
// of work (the loop's tick, the JavaScript step, the SVG raster, the drawer's present, the Core
// calls). These pieces are CPU bound, so wall time is the CPU they cost. Names and times only.
import Foundation

enum Perf {
    private static let lock = NSLock()
    private static var cells: [String: (seconds: Double, count: Int, worst: Double)] = [:]
    // Set by the first sample (the app calls it at start), not lazily at the first diagnostic.
    private static var started = ProcessInfo.processInfo.systemUptime
    private static var startedSet = false
    // The process's CPU seconds (user + system), sampled every few seconds, to say what the last minute cost.
    private static var samples: [(at: Double, cpu: Double)] = []

    static func add(_ name: String, _ seconds: Double) {
        lock.lock()
        var cell = cells[name] ?? (0, 0, 0)
        cell.seconds += seconds
        cell.count += 1
        cell.worst = max(cell.worst, seconds)
        cells[name] = cell
        lock.unlock()
    }

    @discardableResult
    static func time<T>(_ name: String, _ body: () -> T) -> T {
        let begin = ProcessInfo.processInfo.systemUptime
        defer { add(name, ProcessInfo.processInfo.systemUptime - begin) }
        return body()
    }

    /// The CPU seconds this process has used (user + system, every thread).
    static func processCPUSeconds() -> Double {
        var usage = rusage()
        getrusage(RUSAGE_SELF, &usage)
        func seconds(_ value: timeval) -> Double { Double(value.tv_sec) + Double(value.tv_usec) / 1_000_000 }
        return seconds(usage.ru_utime) + seconds(usage.ru_stime)
    }

    /// The process's memory in decimal megabytes (as `ps` reports it): resident, and the footprint macOS charges it.
    static func memoryMB() -> (resident: Double, footprint: Double) {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count) }
        }
        guard result == KERN_SUCCESS else { return (0, 0) }
        return (Double(info.resident_size) / 1_000_000, Double(info.phys_footprint) / 1_000_000)
    }

    /// Called by a timer every few seconds.
    static func sample() {
        lock.lock()
        if !startedSet {
            started = ProcessInfo.processInfo.systemUptime
            startedSet = true
        }
        samples.append((ProcessInfo.processInfo.systemUptime, processCPUSeconds()))
        if samples.count > 200 { samples.removeFirst(samples.count - 200) }
        lock.unlock()
    }

    /// (share of one core over the last minute, since start, uptime in seconds).
    static func cpuShares() -> (lastMinute: Double?, sinceStart: Double, uptime: Double) {
        let now = ProcessInfo.processInfo.systemUptime
        let uptime = max(now - started, 0.001)
        let total = processCPUSeconds()
        lock.lock()
        let recent = samples.first { now - $0.at <= 62 }
        lock.unlock()
        var minute: Double?
        if let recent, now - recent.at > 20 { minute = (total - recent.cpu) / (now - recent.at) * 100 }
        return (minute, total / uptime * 100, uptime)
    }

    /// Name, runs, milliseconds per minute of the app's life, slowest run in milliseconds; the most expensive first.
    static func rows() -> [(name: String, count: Int, msPerMinute: Double, worstMs: Double)] {
        let minutes = max((ProcessInfo.processInfo.systemUptime - started) / 60, 1.0 / 60)
        lock.lock()
        defer { lock.unlock() }
        return cells.map { ($0.key, $0.value.count, $0.value.seconds * 1000 / minutes, $0.value.worst * 1000) }
            .sorted { $0.msPerMinute > $1.msPerMinute }
    }
}
