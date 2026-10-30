// What the Settings window reads from Core, kept fresh while it is open, and
// the strings it shows. Core owns every English and Chinese text it knows
// (L10n); the few that only exist on a Mac come from MacStrings.
import SwiftUI
import AppKit

struct DrawerPrefs: Decodable {
    let theme: String
    let speech: Bool
    let side: String
    let offset: Double
    let locked: Bool
    let fps: Int
    let saveBattery: Bool
    let display: String
}

struct QuotaPrefs: Decodable {
    let interval: Int
    let presets: [Int]
    let showsRemaining: Bool
    let tokenMode: String
    let tokenModeLabel: String
}

struct AgentLook: Decodable {
    let pixel: String
    let classic: String
}

struct AgentModel: Decodable, Identifiable {
    let id: String
    let name: String
    let shown: Bool
    let detected: Bool
    let plan: String?
    let account: String?
    let status: String
    let warning: String?
    let note: String?
    let signIn: String
    let signInHint: String
    let accent: UInt32
    let swatch: UInt32
    let defaultLook: Bool
    let meters: [MeterModel]
    let tokens: TokensModel?
    let look: AgentLook
}

struct HooksState: Decodable {
    let claude: Bool
    let grok: Bool
    let grokConflict: Bool
}

struct CodexAccount: Decodable, Identifiable {
    let label: String
    let email: String?
    var id: String { label }
}

struct CodexState: Decodable {
    let autoSwitch: Bool
    let active: String?
    let accounts: [CodexAccount]
}

struct SettingsModel: Decodable {
    let drawer: DrawerPrefs
    let quota: QuotaPrefs
    let language: String
    let chinese: Bool
    let agents: [AgentModel]
    let hooks: HooksState
    let codex: CodexState
}

struct ModelRow: Decodable {
    let name: String
    let tokens: Int64
    let dollars: Double
    let unpriced: Bool
}

struct PeriodUsage: Decodable {
    let dollars: Double
    let tokens: Int64
    let models: [ModelRow]
}

struct DayPoint: Decodable {
    let date: String
    let tokens: Int64
    let dollars: Double
}

struct UsagePart: Decodable, Identifiable {
    let id: String
    let name: String
    let dollars: Bool
    let scanned: Bool
    let today: PeriodUsage
    let week: PeriodUsage
    let month: PeriodUsage
    let hourly: [Double]
    let hourlyDollars: [Double]
    let days: [DayPoint]
    let monthDays: [DayPoint]
}

struct UsageAgent: Decodable, Identifiable {
    let id: String
    let name: String
    let shown: Bool
    let accent: UInt32
    let swatch: UInt32
    let defaultLook: Bool
    let look: String
    let parts: [UsagePart]
}

struct UsageModel: Decodable {
    let billable: Bool
    let scannedAgo: String?
    let zone: String
    let agents: [UsageAgent]
}

struct LookChoice: Decodable, Identifiable {
    let id: String
    let name: String
    let swatch: UInt32
}

struct LooksModel: Decodable {
    let looks: [LookChoice]
}

final class SettingsStore: ObservableObject {
    @Published var settings: SettingsModel?
    @Published var usage: UsageModel?
    @Published var strings: [String: String] = [:]
    @Published var looks: [String: [LookChoice]] = [:]
    @Published var page: SettingsPage = .agents
    @Published var focus: String?
    @Published var launchAtLogin = LaunchAtLogin.isEnabled
    @Published var hookError: String?
    @Published var skin: Skin = .pixel
    @Published var dark = false

    private var timer: Timer?
    private var loadedFor = ""

    var palette: Palette { Palette(skin: skin, dark: dark) }

    func start() {
        stop()
        refresh()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in self?.refresh() }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
    }

    func refresh() {
        let pixel = AppController.shared.frame?.isPixel ?? true
        skin = pixel ? .pixel : .classic
        dark = DrawerController.isDark
        launchAtLogin = LaunchAtLogin.isEnabled
        let english = pixel
        AppController.shared.request("settings", ["english": english]) { [weak self] reply in
            guard let self, let model = SettingsModel.from(reply) else { return }
            self.settings = model
            self.loadStringsIfNeeded(english: english, chinese: model.chinese)
        }
        if page == .usage {
            AppController.shared.request("usage", ["english": english]) { [weak self] reply in
                if let model = UsageModel.from(reply) { self?.usage = model }
            }
        }
    }

    private func loadStringsIfNeeded(english: Bool, chinese: Bool) {
        let key = "\(english)-\(chinese)"
        guard key != loadedFor else { return }
        loadedFor = key
        AppController.shared.request("tr", ["keys": UIStrings.keys, "english": english]) { [weak self] reply in
            guard let self, let table = reply["strings"] as? [String: String] else { return }
            self.strings = table
        }
    }

    /// A text in the app's language: Core's table first, then the Mac-only one.
    func t(_ key: String) -> String {
        if let known = strings[key], !known.isEmpty { return known }
        if settings?.chinese == true && skin == .classic, let mac = MacStrings.zh[key] { return mac }
        return key
    }

    func t(_ key: String, _ arguments: CVarArg...) -> String {
        var text = t(key)
        for (index, argument) in arguments.enumerated() { text = text.replacingOccurrences(of: "{\(index)}", with: "\(argument)") }
        return text
    }

    func loadLooks(for id: String) {
        let key = "\(id)-\(skin == .pixel ? "pixel" : "classic")"
        guard looks[key] == nil else { return }
        AppController.shared.request("looks", ["id": id, "theme": skin == .pixel ? "pixel" : "classic"]) { [weak self] reply in
            if let model = LooksModel.from(reply) { self?.looks[key] = model.looks }
        }
    }

    func send(_ method: String, _ fields: [String: Any], then: (() -> Void)? = nil) {
        AppController.shared.request(method, fields) { [weak self] _ in
            self?.refresh()
            then?()
        }
    }

    func setDrawer(_ fields: [String: Any]) { send("setDrawer", fields) }
}

enum SettingsPage: String, CaseIterable, Identifiable {
    case agents, drawer, usage, general
    var id: String { rawValue }
    var key: String {
        switch self {
        case .agents: return "Agents"
        case .drawer: return "Drawer"
        case .usage: return "Usage & cost"
        case .general: return "General"
        }
    }
    var pixelIcon: String {
        switch self {
        case .agents: return "agents"
        case .drawer: return "drawer"
        case .usage: return "usage"
        case .general: return "general"
        }
    }
    var symbol: String {
        switch self {
        case .agents: return "person.2"
        case .drawer: return "sidebar.right"
        case .usage: return "chart.bar"
        case .general: return "gearshape"
        }
    }
}

/// The keys Core is asked to translate for the window.
enum UIStrings {
    static let keys: [String] = [
        "Agents", "Drawer", "Usage & cost", "General",
        "Quota, tokens and live activity for every coding agent on this computer, in the drawer's order. Drag a card by its grip or use its arrows to move it; switch one off to hide it everywhere.",
        "The column of characters on the edge of your screen. Hover one for its status, click it for its settings, drag it to move the drawer, right-click it for options.",
        "Characters", "Character style", "Pixel bots drawn for each agent, or the classic shapes you can recolour", "Pixel", "Classic",
        "Character lines", "A character speaks up in a bubble when a session is done, asks you something, needs your approval or hits an error",
        "Placement", "Display", "Automatic (primary display)", "A pinned display falls back to automatic while it is unplugged", "Display {0}", "primary", "Right edge", "Left edge", "Edge", "Which side of the screen the drawer docks to",
        "Center on the edge", "Locked", "Unlocked", "Lock position", "Position",
        "Characters stay put: dragging won't move the drawer, and a character appearing or leaving won't shift the ones above it",
        "Drag any character up or down to move the drawer",
        "Motion", "Frame rate while an agent works", "Calm characters always breathe at 30 fps", "Match the display", "{0} fps",
        "Save battery", "On battery, characters run at 60 fps at most", "Animation",
        "Estimated at API list prices from this computer's session logs — not a bill.",
        "Quota", "Refresh interval", "Providers rate-limit their usage endpoints, so nothing shorter than five minutes is offered", "Every {0} minutes",
        "Quota shows", "How the bars and percentages read on the drawer's cards and in Settings", "Used", "Remaining",
        "Tokens", "Token counting", "Excludes cache reads; new input written to cache still counts. History is kept.",
        "Input + output (excluding cache reads)", "All tokens (including cache)",
        "Startup", "Launch at login", "Language", "App language", "English", "About", "Thanks", "Crash log", "Open folder", "Quit Edgelings", "Quit",
        "The pixel style shows Settings in English",
        "Today", "This month", "On", "Off", "Switch", "Refresh", "Sign in", "Waiting for sign-in", "Scan now", "Not scanned yet",
        "Estimated cost", "Busiest agent", "At API list prices", "No activity yet", "{0} tokens", "no price",
        "Tokens per hour (no price)", "Tokens per day (no price)", "Models", "Models, last 5 hours", "No sessions in this period",
        "Account", "Look", "Save current account…", "Auto-switch when exhausted", "No saved accounts yet", "Remove saved account",
        "The original app, by {0} · MIT License", "The pixel font, by {0} · SIL Open Font License 1.1",
        "Permission hook", "Not detected",
        "Shown", "Hidden", "Move up", "Move down", "Right now", "Account", "Not saved yet",
        "5-hour window", "Weekly window", "Daily window", "Billing cycle", "Monthly window", "Permission prompts",
        "Shows when Claude Code waits for you to approve a tool. Adds a small hook to Claude Code's settings.json that records only the session and the prompt type; sessions started afterwards pick it up.",
        "Shows when Grok waits for you to approve a tool. Adds one hook file, agentisland-notify.json, to ~/.grok/hooks that records only the session and the prompt type; sessions started afterwards pick it up.",
        "A file named agentisland-notify.json is already in ~/.grok/hooks and isn't Edgelings', so this stays off.",
    ]
}

/// Texts only the Mac app has, with their Chinese for the Classic skin
/// (the Pixel skin reads English, as on Windows).
enum MacStrings {
    static let zh: [String: String] = [
        "Start Edgelings when you sign in to your Mac": "登录 Mac 时启动 Edgelings",
        "Same as system": "跟随系统",
        "With Reduce motion on in System Settings, characters slow down and stop following the pointer": "系统设置里开启「减弱动态效果」后，角色会放慢并不再跟随指针",
        "Open System Settings": "打开系统设置",
        "7 days": "7 天",
        "Mac test build": "Mac 测试版",
        "Login Items need your approval in System Settings": "需要在系统设置的「登录项」里批准",
        "Copy diagnostic": "复制诊断信息",
        "Ids, counts and states only: no titles or message text": "只含编号、数量和状态，不含标题或消息文字",
        "Permission hook": "权限提示钩子",
        "Lets the character say when a permission prompt is open. Adds one entry to the agent's settings (a backup is kept); nothing is installed until you switch it on.":
            "让角色在出现权限提示时开口。会在该智能体的设置里加一条（并留备份）；打开之前不会安装任何东西。",
    ]
}
