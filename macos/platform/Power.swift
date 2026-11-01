// Whether this Mac runs on its battery, for the "Save battery" setting. Low
// Power Mode counts too, as the system's own request to spend less. The answer
// is cached for a few seconds: the drawer's loop asks on every frame.
import Foundation
import IOKit.ps

enum Power {
    private static var cached: (at: TimeInterval, battery: Bool) = (0, false)

    /// The providing power source: "Battery Power" or "AC Power" (a desktop
    /// Mac without a battery answers AC). Nil when the system won't say.
    static func source() -> String? {
        guard let blob = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let type = IOPSGetProvidingPowerSourceType(blob)?.takeUnretainedValue() else { return nil }
        return type as String
    }

    static var lowPowerMode: Bool { ProcessInfo.processInfo.isLowPowerModeEnabled }

    /// On battery, in Low Power Mode, or (for the runner's saver measurement) EDGELINGS_ASSUME_BATTERY=1.
    static var onBattery: Bool {
        if ProcessInfo.processInfo.environment["EDGELINGS_ASSUME_BATTERY"] == "1" { return true }
        let now = ProcessInfo.processInfo.systemUptime
        if now - cached.at > 3 || cached.at == 0 {
            cached = (now, source() == "Battery Power" || lowPowerMode)
        }
        return cached.battery
    }
}
