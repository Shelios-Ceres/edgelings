// Swift's side of the bridge to the shared C# Core: the NativeAOT dylib
// (bridge A1) exports ai_call and ai_free, one JSON request in, one JSON reply
// out. See macos/bridge/Shared/BridgeApi.cs for the methods.
import Foundation

final class CoreBridge {
    private typealias CallFunction = @convention(c) (UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>?
    private typealias FreeFunction = @convention(c) (UnsafeMutablePointer<CChar>?) -> Void

    private let call: CallFunction
    private let release: FreeFunction

    /// Loads the dylib; nil with a reason on stderr if it or its exports are missing.
    init?(libraryPath: String) {
        guard let handle = dlopen(libraryPath, RTLD_NOW) else {
            FileHandle.standardError.write(Data("Core library did not load: \(String(cString: dlerror()))\n".utf8))
            return nil
        }
        guard let callSymbol = dlsym(handle, "ai_call"), let freeSymbol = dlsym(handle, "ai_free") else {
            FileHandle.standardError.write(Data("Core library has no ai_call/ai_free\n".utf8))
            return nil
        }
        call = unsafeBitCast(callSymbol, to: CallFunction.self)
        release = unsafeBitCast(freeSymbol, to: FreeFunction.self)
    }

    /// One call. Blocks until Core answers (a scan can take seconds), so the
    /// app calls it off the main thread. An error from Core comes back as
    /// ["error": message]; a reply that is not JSON comes back the same way.
    @discardableResult
    func request(_ method: String, _ fields: [String: Any] = [:]) -> [String: Any] {
        var body = fields
        body["method"] = method
        guard let data = try? JSONSerialization.data(withJSONObject: body),
              let text = String(data: data, encoding: .utf8),
              let reply = call(text) else {
            return ["error": "no reply"]
        }
        defer { release(reply) }
        guard let parsed = try? JSONSerialization.jsonObject(with: Data(String(cString: reply).utf8)) as? [String: Any] else {
            return ["error": "reply was not JSON"]
        }
        return parsed
    }
}
