// The app's link to Core: loads the NativeAOT dylib, polls `frame` about twice
// a second, and hands the answers to whatever draws them. Every call into
// Core happens on one serial queue (a scan can take seconds); results come
// back on the main queue.
import AppKit

final class AppController {
    static let shared = AppController()

    let bridge: CoreBridge?
    private let coreQueue = DispatchQueue(label: "dev.edgelings.core", qos: .userInitiated)
    private var timer: Timer?
    private var polling = false
    private var polls = 0
    private let started = Date()

    /// The latest frame, on the main thread.
    private(set) var frame: FrameModel?
    var onFrame: ((FrameModel) -> Void)?

    // What only the UI knows, sent with each frame so Core decides who may speak.
    var drawerShown = true
    var cardOpen = false
    var cardBot: String?
    var openBubbles: Set<String> = []
    private var dismissed: Set<String> = []

    private init() {
        bridge = CoreBridge(libraryPath: AppController.libraryPath())
        if bridge == nil { NSLog("Edgelings: Core library did not load") }
        // The bundled SQLite lives beside the Core dylib; Core is told where.
        let folder = URL(fileURLWithPath: AppController.libraryPath()).deletingLastPathComponent().path
        bridge?.request("configure", ["native": folder])
    }

    /// Frameworks/ in the bundle, else next to the executable (a loose build).
    static func libraryPath() -> String {
        let bundle = Bundle.main
        let candidates = [
            bundle.privateFrameworksURL?.appendingPathComponent("AgentIslandBridge.dylib").path,
            bundle.executableURL?.deletingLastPathComponent().appendingPathComponent("AgentIslandBridge.dylib").path,
        ].compactMap { $0 }
        return candidates.first { FileManager.default.fileExists(atPath: $0) } ?? candidates.first ?? "AgentIslandBridge.dylib"
    }

    /// Where the bundled character engines are.
    static func enginesDirectory() -> String {
        if let override = ProcessInfo.processInfo.environment["EDGELINGS_ENGINES"] { return override }
        return Bundle.main.resourceURL?.appendingPathComponent("Characters").path ?? "Characters"
    }

    func start() {
        guard bridge != nil, timer == nil else { return }
        coreQueue.async { [bridge] in
            _ = bridge?.request("appStart")
        }
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in self?.poll() }
        poll()
    }

    func markDismissed(_ id: String) { dismissed.insert(id) }

    private func poll() {
        guard let bridge, !polling else { return }
        polling = true
        polls += 1
        var request: [String: Any] = [
            "now": Date().timeIntervalSince(started),
            "shown": drawerShown,
            "cardOpen": cardOpen,
            "bubbles": Array(openBubbles),
            "dismissed": Array(dismissed),
        ]
        if let cardBot { request["cardBot"] = cardBot }
        dismissed.removeAll()
        // Core's presence check wants a fresh window report every few seconds.
        let windows: [String: Any]? = polls % 4 == 1 ? WindowSnapshot.request() : nil
        coreQueue.async { [weak self] in
            if let windows { bridge.request("windows", windows) }
            let reply = bridge.request("frame", request)
            let decoded = FrameModel.from(reply)
            DispatchQueue.main.async {
                guard let self else { return }
                self.polling = false
                guard let decoded else {
                    if let error = reply["error"] { NSLog("Edgelings: frame failed: \(error)") }
                    return
                }
                self.frame = decoded
                self.onFrame?(decoded)
            }
        }
    }

    /// The hover card for one character, or every shown one when id is nil.
    func cards(for id: String?, completion: @escaping (CardsModel?) -> Void) {
        guard let bridge else { completion(nil); return }
        coreQueue.async {
            var request: [String: Any] = [:]
            if let id { request["id"] = id }
            let model = CardsModel.from(bridge.request("card", request))
            DispatchQueue.main.async { completion(model) }
        }
    }

    /// Any other call, off the main thread; the reply comes back on it.
    func request(_ method: String, _ fields: [String: Any] = [:], completion: (([String: Any]) -> Void)? = nil) {
        guard let bridge else { completion?(["error": "no core"]); return }
        coreQueue.async {
            let reply = bridge.request(method, fields)
            if let completion { DispatchQueue.main.async { completion(reply) } }
        }
    }
}
