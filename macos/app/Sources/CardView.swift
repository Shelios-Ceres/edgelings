// The hover card's content in both skins (HoverCard.Content.cs): one section
// per character with its name, phase, tasks, waiting sessions, quota bars,
// tokens and notes; the all-agents card stands the sections in columns.
import SwiftUI

/// Text with each of `numbers`, found in order, lifted to the strong ink.
func emphasised(_ phrase: String, numbers: [String], strong: Color, bold: Bool) -> Text {
    var result = Text("")
    var rest = Substring(phrase)
    for number in numbers where !number.isEmpty {
        guard let range = rest.range(of: number) else { continue }
        result = result + Text(String(rest[rest.startIndex..<range.lowerBound]))
        var lifted = Text(number).foregroundColor(strong)
        if bold { lifted = lifted.fontWeight(.semibold) }
        result = result + lifted
        rest = rest[range.upperBound...]
    }
    return result + Text(String(rest))
}

enum CardMetrics {
    /// How far the pointer or tail reaches past the plate.
    static func reach(_ pixel: Bool) -> CGFloat { pixel ? 4 : 9 }
    /// Room around the plate for the shadow (Classic) or a margin (Pixel).
    static func inset(_ pixel: Bool) -> CGFloat { pixel ? 4 : 20 }
}

struct CardSection: View {
    let card: CardModel
    let palette: Palette

    private var pixel: Bool { palette.skin == .pixel }
    private var indent: CGFloat { pixel ? 14 : 24 }
    private var labelWidth: CGFloat { pixel ? 52 : 40 }
    private var valueWidth: CGFloat { pixel ? 72 : 58 }

    /// Pixel sets every line in one body size; Classic's rows are 12/16 and its phase line 13/18.
    private func row() -> Font { pixel ? PixelFace.font(13) : .system(size: 12) }
    private func head() -> Font { pixel ? PixelFace.font(13) : .system(size: 13) }
    private var secondary: Color { palette.color("Text2") }
    private var tertiary: Color { palette.color("Text3") }
    private var primary: Color { palette.color("Text") }
    private var identity: Color { palette.agentInk(id: card.id, swatch: card.swatch, defaultLook: card.defaultLook) }

    private var who: some View {
        HStack(spacing: 8) {
            if pixel {
                Rectangle().fill(Color(hex: card.swatch)).frame(width: 6, height: 6).padding(.top, 1)
            } else {
                FaceChip(swatch: card.swatch, size: 16)
            }
            SkinText(pixel: pixel, text: card.name, pixelSize: 13, size: 13, weight: .semibold, color: primary)
            if let plan = card.plan, !plan.isEmpty {
                SkinText(pixel: pixel, text: plan, pixelSize: 13, size: 10, weight: .semibold, color: pixel ? identity : primary)
                    .padding(.horizontal, 6).padding(.vertical, 1)
                    .background(pixel ? AnyView(Rectangle().fill(palette.tint(card.swatch))) : AnyView(Capsule().fill(palette.tint(card.swatch))))
            }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if pixel {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 4) {
                        who
                        SkinText(pixel: pixel, text: "·", pixelSize: 13, size: 13, weight: .regular, color: secondary)
                        SkinText(pixel: pixel, text: card.phase, pixelSize: 13, size: 13, weight: .regular, color: secondary)
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        who
                        SkinText(pixel: pixel, text: card.phase, pixelSize: 13, size: 13, weight: .regular, color: secondary).padding(.leading, indent)
                    }
                }
                if let detail = card.detail {
                    SkinText(pixel: pixel, text: detail, pixelSize: 13, size: 12, weight: .regular, color: tertiary).padding(.leading, indent).padding(.top, 1)
                }
            } else {
                who
                (Text(card.phase).foregroundColor(primary) + Text(card.detail.map { " · " + $0 } ?? "").foregroundColor(secondary))
                    .font(head()).padding(.leading, indent).padding(.top, 1)
            }
            if let tasks = card.tasks {
                HStack(alignment: .top, spacing: 8) {
                    SkinText(pixel: pixel, text: tasks.label, pixelSize: 13, size: 12, weight: .regular, color: secondary).frame(width: labelWidth, alignment: .leading)
                    EmphasisedText(pixel: pixel, phrase: tasks.text, numbers: [tasks.number], strong: primary, base: secondary, pixelSize: 13, size: 12)
                }
                .padding(.leading, indent).padding(.top, 7)
            }
            if !card.waiting.rows.isEmpty { waitingList.padding(.leading, indent).padding(.top, card.tasks == nil ? 7 : 4) }
            if !card.meters.isEmpty {
                VStack(spacing: 6) {
                    ForEach(Array(card.meters.enumerated()), id: \.offset) { _, meter in meterRow(meter) }
                }
                .padding(.leading, indent).padding(.top, (card.tasks == nil && card.waiting.rows.isEmpty) ? 7 : 4)
            }
            if let tokens = card.tokens { tokensRow(tokens).padding(.leading, indent).padding(.top, 4) }
            if !card.note.isEmpty {
                VStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(card.note.enumerated()), id: \.offset) { _, note in
                        VStack(alignment: .leading, spacing: 0) {
                            if let lead = note.lead { SkinText(pixel: pixel, text: lead, pixelSize: 13, size: 12, weight: .regular, color: tertiary) }
                            EmphasisedText(pixel: pixel, phrase: note.text, numbers: note.numbers, strong: primary, base: tertiary, pixelSize: 13, size: 12)
                        }
                    }
                }
                .padding(.leading, indent).padding(.top, pixel ? 5 : 6)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
    }

    private var waitingList: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(Array(card.waiting.rows.enumerated()), id: \.offset) { _, row in
                HStack(alignment: .top, spacing: 8) {
                    SkinText(pixel: pixel, text: row.title, pixelSize: 13, size: 12, weight: .regular, color: pixel ? secondary : primary, lines: 2)
                    Spacer(minLength: 0)
                    SkinText(pixel: pixel, text: row.word, pixelSize: 13, size: 12, weight: .regular, color: wordColor(row))
                }
            }
            if card.waiting.more > 0 {
                SkinText(pixel: pixel, text: "+\(card.waiting.more) more", pixelSize: 13, size: 12, weight: .regular, color: pixel ? secondary : tertiary)
            }
        }
    }

    private func wordColor(_ row: WaitingRowModel) -> Color {
        if pixel { return row.kind == "error" ? palette.color("Critical") : tertiary }
        switch row.kind {
        case "approval": return palette.color("Caution")
        case "question": return palette.color("AccentText")
        case "error", "taskskipped": return palette.color("Critical")
        default: return secondary
        }
    }

    private func meterRow(_ meter: MeterModel) -> some View {
        let warn = (meter.used ?? 0) >= 0.9
        return HStack(spacing: 8) {
            SkinText(pixel: pixel, text: meter.label, pixelSize: 13, size: 12, weight: .regular, color: secondary, lines: 1).frame(width: labelWidth, alignment: .leading)
            Group {
                if pixel {
                    PixelBar(share: meter.shown, color: warn ? palette.color("Critical") : identity, blocks: 14)
                } else {
                    ClassicBar(share: meter.shown, color: warn ? palette.color("Critical") : identity, track: palette.tint(card.swatch))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            SkinText(pixel: pixel, text: meter.value, pixelSize: 13, size: 12, weight: warn && !pixel ? .semibold : .regular,
                     color: warn ? palette.color("Critical") : secondary)
                .frame(width: valueWidth, alignment: .trailing)
        }
    }

    private func tokensRow(_ tokens: TokensModel) -> some View {
        HStack(alignment: .top, spacing: 8) {
            SkinText(pixel: pixel, text: tokens.label, pixelSize: 13, size: 12, weight: .regular, color: secondary).frame(width: labelWidth, alignment: .leading)
            if tokens.todayNumber.isEmpty {
                SkinText(pixel: pixel, text: tokens.today, pixelSize: 13, size: 12, weight: .regular, color: tertiary)
            } else {
                VStack(alignment: .leading, spacing: 2) {
                    HStack { SkinText(pixel: pixel, text: "Today", pixelSize: 13, size: 12, weight: .regular, color: secondary); Spacer(); EmphasisedText(pixel: pixel, phrase: tokens.todayNumber, numbers: [tokens.todayNumber], strong: primary, base: primary, pixelSize: 13, size: 12) }
                    HStack { SkinText(pixel: pixel, text: "This month", pixelSize: 13, size: 12, weight: .regular, color: secondary); Spacer(); EmphasisedText(pixel: pixel, phrase: tokens.monthNumber, numbers: [tokens.monthNumber], strong: primary, base: primary, pixelSize: 13, size: 12) }
                }
            }
        }
    }
}

/// The card: sections in columns that fit the room, in the skin's panel. A
/// one-character card has a pointer or tail aimed at its character; the
/// all-agents card has none.
struct CardView: View {
    let model: CardsModel
    let palette: Palette
    /// Columns of section indexes, chosen by CardPresenter.
    let columns: [[Int]]
    var tailY: CGFloat?
    var rightEdge = true

    private var pixel: Bool { palette.skin == .pixel }
    private var pointed: Bool { model.cards.count == 1 }

    var column: CGFloat { (pixel ? 272 : 264) - 32 }

    var body: some View {
        let reach = CardMetrics.reach(pixel)
        let inset = CardMetrics.inset(pixel)
        let content = HStack(alignment: .top, spacing: 0) {
            ForEach(Array(columns.enumerated()), id: \.offset) { position, indexes in
                if position > 0 {
                    verticalRule.padding(.horizontal, 16)
                }
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(indexes.enumerated()), id: \.offset) { row, index in
                        if row > 0 { rule.padding(.vertical, pixel ? 9 : 10) }
                        CardSection(card: model.cards[index], palette: palette)
                    }
                }
                .frame(width: column, alignment: .topLeading)
            }
        }
        .padding(.leading, 16).padding(.trailing, 16).padding(.top, pixel ? 14 : 13).padding(.bottom, 14)
        return content
            .background(plate)
            .padding(.leading, rightEdge ? 0 : reach).padding(.trailing, rightEdge ? reach : 0)
            .padding(inset)
    }

    @ViewBuilder private var plate: some View {
        if pixel {
            PixelPlateBackground(palette: palette, pointerY: pointed ? tailY : nil, pointLeft: !rightEdge)
        } else {
            ClassicPlate(palette: palette, radius: 16, tailY: pointed ? tailY : nil, pointsRight: rightEdge)
        }
    }

    @ViewBuilder private var rule: some View {
        if pixel {
            DottedRule(color: palette.color("LineSoft"), vertical: false)
        } else {
            Rectangle().fill(palette.color("LineSoft")).frame(height: 1)
        }
    }

    @ViewBuilder private var verticalRule: some View {
        if pixel {
            DottedRule(color: palette.color("LineSoft"), vertical: true)
        } else {
            Rectangle().fill(palette.color("LineSoft")).frame(width: 1)
        }
    }
}

/// A faint dotted divider: one-cell dots, one cell apart.
struct DottedRule: View {
    let color: Color
    let vertical: Bool
    var dash: [CGFloat] = [2, 2]

    var body: some View {
        Canvas { context, size in
            var path = Path()
            if vertical {
                path.move(to: CGPoint(x: 1, y: 0)); path.addLine(to: CGPoint(x: 1, y: size.height))
            } else {
                path.move(to: CGPoint(x: 0, y: 1)); path.addLine(to: CGPoint(x: size.width, y: 1))
            }
            context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: 2, dash: dash))
        }
        .frame(width: vertical ? 2 : nil, height: vertical ? nil : 2)
    }
}

/// Classic's paper plate: a rounded card with a hairline, the soft tail where
/// there is one, and the two shadows Windows gives it.
struct ClassicPlate: View {
    let palette: Palette
    var radius: CGFloat
    var tailY: CGFloat?
    var pointsRight: Bool

    var body: some View {
        GeometryReader { proxy in
            ZStack {
                RoundedRectangle(cornerRadius: min(radius, proxy.size.height / 2)).fill(palette.color("Paper"))
                if let tailY {
                    let half: CGFloat = radius > 12 ? 10 : 6
                    let reach: CGFloat = radius > 12 ? 9 : 7
                    let y = min(max(tailY, radius + half), max(radius + half, proxy.size.height - radius - half))
                    let rect = pointsRight
                        ? CGRect(x: proxy.size.width - 0.5, y: y - half, width: reach, height: half * 2)
                        : CGRect(x: -reach + 0.5, y: y - half, width: reach, height: half * 2)
                    SoftTail(pointsRight: pointsRight, halfBase: half, reach: reach).path(in: rect).fill(palette.color("Paper"))
                }
            }
            .compositingGroup()
            .shadow(color: .black.opacity(palette.dark ? 0.45 : 0.13), radius: palette.dark ? 8 : 7, x: 0, y: 8)
            .shadow(color: .black.opacity(palette.dark ? 0.35 : 0.10), radius: 1, x: 0, y: 1)
            RoundedRectangle(cornerRadius: min(radius, proxy.size.height / 2)).stroke(palette.hairline, lineWidth: 1)
        }
    }
}
