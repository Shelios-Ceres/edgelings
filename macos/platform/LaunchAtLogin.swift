// Launch at login for the app's own bundle, through SMAppService (macOS 13+).
// It works only from a signed (or ad-hoc signed) .app bundle in a normal
// location, so it is exercised by the friend's check, not by CI.
import ServiceManagement

enum LaunchAtLogin {
    static var status: SMAppService.Status { SMAppService.mainApp.status }

    /// Registered and approved. A registration the user switched off in
    /// System Settings > General > Login Items reads as requiresApproval.
    static var isEnabled: Bool { status == .enabled }

    static var needsApproval: Bool { status == .requiresApproval }

    /// Returns whether the change took.
    @discardableResult
    static func setEnabled(_ on: Bool) -> Bool {
        do {
            if on {
                if status != .enabled { try SMAppService.mainApp.register() }
            } else if status != .notRegistered {
                try SMAppService.mainApp.unregister()
            }
            return true
        } catch {
            return false
        }
    }
}
