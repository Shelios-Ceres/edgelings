// Offscreen renders of the Settings pages from synthetic data (RenderMode's
// second half). The strings come from the real Core, so the Chinese pages
// read as the app does; everything else is invented.
import SwiftUI
import AppKit

enum RenderSettings {
    private static let settingsTemplate = """
    {"drawer":{"theme":"pixel","speech":true,"side":"right","offset":0.5,"locked":false,"fps":60,"saveBattery":true,"display":"auto"},
     "quota":{"interval":300,"presets":[300,900,1800],"showsRemaining":false,"tokenMode":"all","tokenModeLabel":"All tokens (including cache)"},
     "language":"auto","chinese":false,
     "agents":[
      {"id":"claude","name":"Claude","shown":true,"detected":true,"plan":"MAX","account":"work","status":"synced 2m ago","signIn":"none","signInHint":"","accent":14120791,"swatch":@claude@,"defaultLook":true,
       "meters":[{"label":"5h","value":"38%","shown":0.38,"used":0.38,"resets":"Resets in 2h 10m"},{"label":"Week","value":"12%","shown":0.12,"used":0.12}],
       "tokens":{"label":"Tokens","today":"today 1.2M","todayNumber":"1.2M","month":"48M this month","monthNumber":"48M"},
       "look":{"pixel":"clawd","classic":"succulent"}},
      {"id":"codex","name":"Codex","shown":true,"detected":true,"plan":"PRO","account":"personal","status":"synced 4m ago","warning":"Quota updated 12 min ago","signIn":"available","signInHint":"Opens a terminal running codex login","accent":10396075,"swatch":@codex@,"defaultLook":true,
       "meters":[{"label":"5h","value":"93%","shown":0.93,"used":0.93},{"label":"Week","value":"61%","shown":0.61,"used":0.61}],
       "tokens":{"label":"Tokens","today":"today 310K","todayNumber":"310K","month":"9.8M this month","monthNumber":"9.8M"},
       "look":{"pixel":"notebook","classic":"desk-computer"}},
      {"id":"kimi","name":"Kimi Code","shown":false,"detected":true,"status":"Waiting for the first sync","signIn":"none","signInHint":"","accent":3043319,"swatch":@kimi@,"defaultLook":true,
       "meters":[],"look":{"pixel":"moonlet","classic":"moon-lamp"}}],
     "hooks":{"claude":false,"grok":false,"grokConflict":false},
     "codex":{"autoSwitch":false,"active":"personal","accounts":[{"label":"personal","email":"a@example.com"},{"label":"work"}]}}
    """

    private static let usageTemplate = """
    {"billable":false,"scannedAgo":"3 min ago","zone":"Eastern Daylight Time (UTC−04:00)","agents":[
     {"id":"claude","name":"Claude","shown":true,"accent":14120791,"swatch":@claude@,"defaultLook":true,"look":"@claudeLook@","parts":[{"id":"claude","name":"Claude","dollars":true,"scanned":true,
       "today":{"dollars":4.21,"tokens":1200000,"models":[{"name":"claude-opus-5-5","tokens":900000,"dollars":3.9,"unpriced":false},{"name":"claude-haiku-5-5","tokens":300000,"dollars":0.31,"unpriced":false}]},
       "week":{"dollars":22.5,"tokens":9100000,"models":[{"name":"claude-opus-5-5","tokens":7000000,"dollars":20.1,"unpriced":false}]},
       "month":{"dollars":88.2,"tokens":48000000,"models":[{"name":"claude-opus-5-5","tokens":40000000,"dollars":80.1,"unpriced":false}]},
       "hourly":[0,0,0,0,0,0,0,0,12,40,90,60,10,0,20,80,120,60,30,0,0,0,0,0],
       "hourlyDollars":[0,0,0,0,0,0,0,0,0.1,0.5,1.2,1.8,1.9,1.9,2.2,3,4,4.1,4.21,4.21,4.21,4.21,4.21,4.21],
       "days":[{"date":"10-24","tokens":1,"dollars":2},{"date":"10-25","tokens":1,"dollars":4},{"date":"10-26","tokens":1,"dollars":1},{"date":"10-27","tokens":1,"dollars":6},{"date":"10-28","tokens":1,"dollars":3},{"date":"10-29","tokens":1,"dollars":2.5},{"date":"10-30","tokens":1,"dollars":4.2}],
       "monthDays":[{"date":"10-01","tokens":1,"dollars":3},{"date":"10-02","tokens":1,"dollars":5},{"date":"10-03","tokens":1,"dollars":2}]}]},
     {"id":"kimi","name":"Kimi Code","shown":true,"accent":3043319,"swatch":@kimi@,"defaultLook":true,"look":"@kimiLook@","parts":[{"id":"kimi","name":"Kimi Code","dollars":true,"scanned":true,
       "today":{"dollars":0.4,"tokens":300000,"models":[{"name":"K3.1 Pro","tokens":200000,"dollars":0.3,"unpriced":false},{"name":"Memory (background)","tokens":100000,"dollars":0.1,"unpriced":true}]},
       "week":{"dollars":1,"tokens":900000,"models":[]},"month":{"dollars":3,"tokens":3100000,"models":[]},
       "hourly":[],"hourlyDollars":[0,0,0,0,0,0,0,0,0,0,0,0,0.1,0.2,0.4,0.4,0.4,0.4,0.4,0.4,0.4,0.4,0.4,0.4],"days":[],"monthDays":[]}]}]}
    """

    private static func fill(_ template: String, _ skin: Skin) -> String {
        var text = template
        for id in ["claude", "codex", "kimi"] {
            let look = SampleLooks.look(id, skin)
            text = text.replacingOccurrences(of: "@\(id)@", with: String(look.swatch))
            text = text.replacingOccurrences(of: "@\(id)Look@", with: look.id)
        }
        return text
    }

    /// The fixtures are written in English; the words Core owns go through its table, so the Chinese
    /// renders read as the app does. Relative times and unit words are the only invented parts.
    private static func localize(_ json: String, _ store: SettingsStore, chinese: Bool) -> String {
        func t(_ key: String, _ args: String...) -> String { args.enumerated().reduce(store.t(key)) { $0.replacingOccurrences(of: "{\($1.offset)}", with: $1.element) } }
        var text = json
        let agoShort = chinese ? "2分钟前" : "2m ago"
        let table: [(String, String)] = [
            ("\"synced 2m ago\"", "\"" + t("synced {0}", agoShort) + "\""),
            ("\"synced 4m ago\"", "\"" + t("synced {0}", chinese ? "4分钟前" : "4m ago") + "\""),
            ("\"Quota updated 12 min ago\"", "\"" + t("Quota updated {0}", chinese ? "12分钟前" : "12 min ago") + "\""),
            ("\"Waiting for the first sync\"", "\"" + t("Waiting for the first sync") + "\""),
            ("\"Resets in 2h 10m\"", "\"" + t("Resets {0}", chinese ? "2小时10分钟后" : "in 2h 10m") + "\""),
            ("\"label\":\"5h\"", "\"label\":\"" + t("5h") + "\",\"title\":\"" + t("5-hour window") + "\""),
            ("\"label\":\"Week\"", "\"label\":\"" + t("Week") + "\",\"title\":\"" + t("Weekly window") + "\""),
            ("\"Tokens\"", "\"" + t("Tokens") + "\""),
            ("\"today 1.2M\"", "\"" + t("today") + " 1.2M\""),
            ("\"today 310K\"", "\"" + t("today") + " 310K\""),
            ("\"48M this month\"", "\"" + t("{0} this month", "48M") + "\""),
            ("\"9.8M this month\"", "\"" + t("{0} this month", "9.8M") + "\""),
            ("\"scannedAgo\":\"3 min ago\"", "\"scannedAgo\":\"" + (chinese ? "3分钟前" : "3 min ago") + "\""),
            ("Memory (background)", t("Memory (background)")),
        ]
        for (from, to) in table { text = text.replacingOccurrences(of: from, with: to) }
        return text
    }

    @MainActor
    static func run(into url: URL, save: (NSImage?, String) -> Void) {
        for (language, tag) in [("en", "en"), ("zh", "zh")] {
            // Core's strings, in the language asked for (a temp settings file, never the user's).
            AppController.shared.bridge?.request("set", ["language": language])
            for skin in [Skin.pixel, Skin.classic] {
                for dark in [false, true] {
                    let store = SettingsStore()
                    store.skin = skin
                    store.dark = dark
                    let english = skin == .pixel
                    if let bridge = AppController.shared.bridge,
                       let table = bridge.request("tr", ["keys": UIStrings.keys, "english": english])["strings"] as? [String: String] {
                        store.strings = table
                    }
                    let chinese = language == "zh" && !english
                    var settingsText = localize(fill(settingsTemplate, skin), store, chinese: chinese)
                    if chinese { settingsText = settingsText.replacingOccurrences(of: "\"chinese\":false", with: "\"chinese\":true") }
                    store.settings = SettingsModel.from(parse(settingsText))
                    store.usage = UsageModel.from(parse(localize(fill(usageTemplate, skin), store, chinese: chinese)))
                    store.launchAtLogin = false
                    for id in ["claude", "codex", "kimi"] {
                        for theme in ["pixel", "classic"] {
                            if let model = LooksModel.from(AppController.shared.bridge?.request("looks", ["id": id, "theme": theme, "english": theme == "pixel"]) ?? [:]) {
                                store.looks["\(id)-\(theme)"] = model.looks
                            }
                        }
                    }
                    let name = "\(skin == .pixel ? "pixel" : "classic")-\(dark ? "dark" : "light")-\(language == "zh" && !english ? "zh" : "en")"
                    if language == "zh" && english { continue }
                    for page in SettingsPage.allCases {
                        store.page = page
                        let content = SettingsPageBody(store: store)
                            .padding(24).frame(width: 720, alignment: .leading)
                            .background(store.palette.color("Desk"))
                            .fixedSize(horizontal: false, vertical: true)
                        let renderer = ImageRenderer(content: content)
                        renderer.scale = 2
                        save(renderer.nsImage, "settings-\(page.rawValue)-\(name).png")
                    }
                    // The whole window, with its sidebar.
                    store.page = .agents
                    let window = SettingsRoot(store: store).frame(width: 880, alignment: .topLeading).fixedSize(horizontal: false, vertical: true)
                    let windowRenderer = ImageRenderer(content: window)
                    windowRenderer.scale = 2
                    save(windowRenderer.nsImage, "settings-window-\(name).png")
                }
            }
        }
        AppController.shared.bridge?.request("set", ["language": "en"])
    }

    private static func parse(_ json: String) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any]) ?? [:]
    }
}
