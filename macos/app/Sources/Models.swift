// What Core's `frame` and `card` calls answer, as Swift values. All text is
// already chosen and localised by Core (the Pixel set reads English).
import Foundation
import CoreGraphics

struct SpeechModel: Decodable {
    let line: String
    /// done, waiting, error or plain: the pop each kind of line gets.
    let kind: String
    let linger: Double?
    let phaseLine: Bool
    /// Classic's one-shot cue to play as the bubble pops.
    let cue: String?
    /// Pixel: hop the character as the bubble lands.
    let hop: Bool?
}

struct BotModel: Decodable, Identifiable {
    let id: String
    let name: String
    /// DrawerLooks.Kind, the engine's name for the character.
    let kind: String
    let visible: Bool
    let look: String
    let swatch: UInt32
    let tempo: Double
    let phase: String
    let entry: String
    let hold: String
    let oneShot: Bool
    let resting: Bool
    let running: Int
    let seq: Int
    let detail: String?
    let speech: SpeechModel?
    let takeBack: Bool?
}

struct FrameModel: Decodable {
    let theme: String
    let side: String
    let offset: Double
    let locked: Bool
    let pinnedTop: Double?
    let fps: Int
    let saveBattery: Bool
    let speechBubbles: Bool
    let chinese: Bool
    let order: [String]
    let bots: [BotModel]

    var isPixel: Bool { theme == "pixel" }
    var shownBots: [BotModel] {
        bots.filter { $0.visible }.sorted { (order.firstIndex(of: $0.id) ?? 99) < (order.firstIndex(of: $1.id) ?? 99) }
    }
}

struct MeterModel: Decodable {
    let label: String
    let value: String
    let shown: Double?
    let used: Double?
    let resets: String?
}

struct TasksModel: Decodable {
    let label: String
    let text: String
    let number: String
}

struct TokensModel: Decodable {
    let label: String
    let today: String
    let todayNumber: String
    let month: String
    let monthNumber: String
}

struct PhraseModel: Decodable {
    let text: String
    let lead: String?
    let numbers: [String]
}

struct WaitingRowModel: Decodable {
    let title: String
    let word: String
    let kind: String
}

struct WaitingModel: Decodable {
    let more: Int
    let rows: [WaitingRowModel]
}

struct CardModel: Decodable, Identifiable {
    let id: String
    let name: String
    let swatch: UInt32
    let phase: String
    let detail: String?
    let plan: String?
    let tasks: TasksModel?
    let meters: [MeterModel]
    let tokens: TokensModel?
    let note: [PhraseModel]
    let waiting: WaitingModel
}

struct CardsModel: Decodable {
    let pixel: Bool
    let cards: [CardModel]
}

extension Decodable {
    /// Decodes a bridge reply (a dictionary from CoreBridge.request).
    static func from(_ reply: [String: Any]) -> Self? {
        guard JSONSerialization.isValidJSONObject(reply),
              let data = try? JSONSerialization.data(withJSONObject: reply) else { return nil }
        return try? JSONDecoder().decode(Self.self, from: data)
    }
}

/// 0xRRGGBB as a Core Graphics colour.
func rgb(_ value: UInt32, alpha: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
            blue: CGFloat(value & 0xFF) / 255, alpha: alpha)
}
