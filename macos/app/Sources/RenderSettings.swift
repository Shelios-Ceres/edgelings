// Offscreen renders of the Settings pages from synthetic data (RenderMode's
// second half). The strings come from the real Core, so the Chinese pages
// read as the app does; everything else is invented.
import SwiftUI
import AppKit

enum RenderSettings {
    private static let settingsJSON = """
    {"drawer":{"theme":"pixel","speech":true,"side":"right","offset":0.5,"locked":false,"fps":60,"saveBattery":true,"display":"auto"},
     "quota":{"interval":300,"presets":[300,900,1800],"showsRemaining":false,"tokenMode":"all","tokenModeLabel":"All tokens (including cache)"},
     "language":"auto","chinese":false,
     "agents":[
      {"id":"claude","name":"Claude","shown":true,"detected":true,"plan":"MAX","account":"work","status":"synced 2m ago","signIn":"none","signInHint":"","accent":14120791,
       "meters":[{"label":"5h","value":"38%","shown":0.38,"used":0.38,"resets":"Resets in 2h 10m"},{"label":"Week","value":"12%","shown":0.12,"used":0.12}],
       "tokens":{"label":"Tokens","today":"today 1.2M","todayNumber":"1.2M","month":"48M this month","monthNumber":"48M"},
       "look":{"pixel":"clawd","classic":"succulent"}},
      {"id":"codex","name":"Codex","shown":true,"detected":true,"plan":"PRO","account":"personal","status":"synced 4m ago","warning":"Quota updated 12 min ago","signIn":"available","signInHint":"Opens a terminal running codex login","accent":10396075,
       "meters":[{"label":"5h","value":"93%","shown":0.93,"used":0.93},{"label":"Week","value":"61%","shown":0.61,"used":0.61}],
       "tokens":{"label":"Tokens","today":"today 310K","todayNumber":"310K","month":"9.8M this month","monthNumber":"9.8M"},
       "look":{"pixel":"notebook","classic":"desk-computer"}},
      {"id":"kimi","name":"Kimi Code","shown":false,"detected":true,"status":"Waiting for the first sync","signIn":"none","signInHint":"","accent":3043319,
       "meters":[],"look":{"pixel":"moonlet","classic":"moon-lamp"}}],
     "hooks":{"claude":false,"grok":false,"grokConflict":false},
     "codex":{"autoSwitch":false,"active":"personal","accounts":[{"label":"personal","email":"a@example.com"},{"label":"work"}]}}
    """

    private static let usageJSON = """
    {"billable":false,"scannedAgo":"Last scanned 3 min ago","zone":"Eastern Daylight Time (UTC−04:00)","agents":[
     {"id":"claude","name":"Claude","shown":true,"accent":14120791,"parts":[{"id":"claude","name":"Claude","dollars":true,"scanned":true,
       "today":{"dollars":4.21,"tokens":1200000,"models":[{"name":"claude-opus-5-5","tokens":900000,"dollars":3.9,"unpriced":false},{"name":"claude-haiku-5-5","tokens":300000,"dollars":0.31,"unpriced":false}]},
       "week":{"dollars":22.5,"tokens":9100000,"models":[{"name":"claude-opus-5-5","tokens":7000000,"dollars":20.1,"unpriced":false}]},
       "month":{"dollars":88.2,"tokens":48000000,"models":[{"name":"claude-opus-5-5","tokens":40000000,"dollars":80.1,"unpriced":false}]},
       "hourly":[0,0,0,0,0,0,0,0,12,40,90,60,10,0,20,80,120,60,30,0,0,0,0,0],
       "hourlyDollars":[0,0,0,0,0,0,0,0,0.1,0.5,1.2,1.8,1.9,1.9,2.2,3,4,4.1,4.21,4.21,4.21,4.21,4.21,4.21],
       "days":[{"date":"10-24","tokens":1,"dollars":2},{"date":"10-25","tokens":1,"dollars":4},{"date":"10-26","tokens":1,"dollars":1},{"date":"10-27","tokens":1,"dollars":6},{"date":"10-28","tokens":1,"dollars":3},{"date":"10-29","tokens":1,"dollars":2.5},{"date":"10-30","tokens":1,"dollars":4.2}],
       "monthDays":[{"date":"10-01","tokens":1,"dollars":3},{"date":"10-02","tokens":1,"dollars":5},{"date":"10-03","tokens":1,"dollars":2}]}]},
     {"id":"kimi","name":"Kimi Code","shown":true,"accent":3043319,"parts":[{"id":"kimi","name":"Kimi Code","dollars":true,"scanned":true,
       "today":{"dollars":0.4,"tokens":300000,"models":[{"name":"K3.1 Pro","tokens":200000,"dollars":0.3,"unpriced":false},{"name":"Memory (background)","tokens":100000,"dollars":0.1,"unpriced":true}]},
       "week":{"dollars":1,"tokens":900000,"models":[]},"month":{"dollars":3,"tokens":3100000,"models":[]},
       "hourly":[],"hourlyDollars":[0,0,0,0,0,0,0,0,0,0,0,0,0.1,0.2,0.4,0.4,0.4,0.4,0.4,0.4,0.4,0.4,0.4,0.4],"days":[],"monthDays":[]}]}]}
    """

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
                    var settings = SettingsModel.from(parse(settingsJSON))
                    if language == "zh", !english { settings = SettingsModel.from(parse(settingsJSON.replacingOccurrences(of: "\"chinese\":false", with: "\"chinese\":true"))) }
                    store.settings = settings
                    store.usage = UsageModel.from(parse(usageJSON))
                    store.launchAtLogin = false
                    store.looks["claude-pixel"] = [LookChoice(id: "clawd", name: "Clawd", swatch: 0xD97757), LookChoice(id: "crab", name: "Crab", swatch: 0xD97757)]
                    store.looks["claude-classic"] = [LookChoice(id: "succulent", name: "Succulent", swatch: 0xD97757), LookChoice(id: "clay-timer", name: "Clay timer", swatch: 0xD97757)]
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
                }
            }
        }
        AppController.shared.bridge?.request("set", ["language": "en"])
    }

    private static func parse(_ json: String) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any]) ?? [:]
    }
}
