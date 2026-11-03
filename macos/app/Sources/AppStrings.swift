// The words the menus and the one-off bubbles need outside the Settings window,
// in the app's language: Core's table first, then the few only a Mac has.
import Foundation

enum AppStrings {
    static let keys = [
        "Settings…", "Quit Edgelings", "Show drawer", "Hide drawer", "Lock position", "Unlock position",
        "Move to left edge", "Move to right edge", "Classic characters", "Pixel characters", "Character lines",
        "Shown agents", "Look", "Locked in place.\nRight-click to unlock.",
    ]

    private static let zh: [String: String] = [
        "Unlock position": "取消固定位置",
        "Move to left edge": "贴到屏幕左边",
        "Move to right edge": "贴到屏幕右边",
        "Classic characters": "经典角色",
        "Pixel characters": "像素角色",
    ]

    private static var table: [String: String] = [:]
    private static var chinese = false

    /// Called with each frame: the table is fetched again when the language changes.
    static func follow(chinese now: Bool, changed: (() -> Void)? = nil) {
        guard now != chinese || table.isEmpty else { return }
        chinese = now
        AppController.shared.request("tr", ["keys": keys]) { reply in
            guard let strings = reply["strings"] as? [String: String] else { return }
            table = strings
            changed?()
        }
    }

    static func t(_ key: String) -> String {
        if let known = table[key], !known.isEmpty, known != key { return known }
        if chinese, let mac = zh[key] { return mac }
        return known(key)
    }

    private static func known(_ key: String) -> String { table[key] ?? key }
}
