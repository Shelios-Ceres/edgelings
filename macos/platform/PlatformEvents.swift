// Sleep, wake and unlock, forwarded to Core so usage refreshes the way it does
// on Windows (UsageStore.HandleWake / HandleUnlock).
import AppKit

final class PlatformEvents {
    private var tokens: [(center: NotificationCenter, token: NSObjectProtocol)] = []
    private let handler: (String) -> Void

    /// handler receives "sleep", "wake", "lock" or "unlock".
    init(handler: @escaping (String) -> Void) {
        self.handler = handler
    }

    /// Sends every event to Core as {"method":"systemEvent","kind":…}.
    convenience init(forwardingTo bridge: CoreBridge) {
        self.init { kind in
            DispatchQueue.global(qos: .utility).async { bridge.request("systemEvent", ["kind": kind]) }
        }
    }

    func start() {
        guard tokens.isEmpty else { return }
        let workspace = NSWorkspace.shared.notificationCenter
        observe(workspace, NSWorkspace.willSleepNotification, "sleep")
        observe(workspace, NSWorkspace.didWakeNotification, "wake")
        // Fast user switching back to this session counts as coming back.
        observe(workspace, NSWorkspace.sessionDidBecomeActiveNotification, "unlock")
        // The lock screen has no public notification; these are the ones the
        // upstream Mac app listens to as well.
        let distributed = DistributedNotificationCenter.default()
        observe(distributed, Notification.Name("com.apple.screenIsLocked"), "lock")
        observe(distributed, Notification.Name("com.apple.screenIsUnlocked"), "unlock")
    }

    func stop() {
        for (center, token) in tokens { center.removeObserver(token) }
        tokens.removeAll()
    }

    private func observe(_ center: NotificationCenter, _ name: Notification.Name, _ kind: String) {
        let token = center.addObserver(forName: name, object: nil, queue: .main) { [handler] _ in handler(kind) }
        tokens.append((center, token))
    }

    deinit { stop() }
}
