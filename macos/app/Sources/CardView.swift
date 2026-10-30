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

struct CardSection: View {
    let card: CardModel
    let palette: Palette

    private var pixel: Bool { palette.skin == .pixel }
    private var indent: CGFloat { pixel ? 14 : 24 }
    private var labelWidth: CGFloat { pixel ? 52 : 40 }
    private var valueWidth: CGFloat { pixel ? 72 : 58 }

    private func face(_ size: CGFloat = 12) -> Font { pixel ? PixelFace.font(size == 12 ? 13 : size) : .system(size: size) }
    private var secondary: Color { palette.color("Text2") }
    private var tertiary: Color { palette.color("Text3") }
    private var primary: Color { palette.color("Text") }
    private var identity: Color { palette.readable(card.swatch) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                if pixel {
                    Rectangle().fill(Color(hex: card.swatch)).frame(width: 6, height: 6).padding(.top, 1)
                } else {
                    Circle().fill(Color(hex: card.swatch)).frame(width: 16, height: 16)
                }
                Text(card.name).font(pixel ? PixelFace.font(13) : .system(size: 13, weight: .semibold)).foregroundColor(primary)
                if let plan = card.plan, !plan.isEmpty {
                    Text(plan.uppercased())
                        .font(pixel ? PixelFace.font(10) : .system(size: 10, weight: .semibold))
                        .foregroundColor(identity)
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .background(RoundedRectangle(cornerRadius: pixel ? 0 : 4).fill(palette.tint(card.swatch)))
                }
                if pixel {
                    Text(card.phase).font(face()).foregroundColor(secondary).lineLimit(2)
                }
            }
            if pixel {
                if let detail = card.detail {
                    Text(detail).font(face()).foregroundColor(tertiary).padding(.leading, indent).padding(.top, 1)
                }
            } else {
                let status = ([card.phase] + (card.detail.map { [$0] } ?? [])).joined(separator: " · ")
                Text(status).font(face()).foregroundColor(secondary).padding(.leading, indent).padding(.top, 1)
            }
            if let tasks = card.tasks {
                HStack(alignment: .top, spacing: 8) {
                    Text(tasks.label).font(face()).foregroundColor(secondary).frame(width: labelWidth, alignment: .leading)
                    emphasised(tasks.text, numbers: [tasks.number], strong: primary, bold: !pixel).font(face()).foregroundColor(tertiary)
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
                            if let lead = note.lead { Text(lead).font(face()).foregroundColor(tertiary) }
                            emphasised(note.text, numbers: note.numbers, strong: primary, bold: !pixel).font(face()).foregroundColor(tertiary)
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
                    Text(row.title).font(face()).foregroundColor(pixel ? secondary : primary).lineLimit(2)
                    Spacer(minLength: 0)
                    Text(row.word).font(face()).foregroundColor(wordColor(row))
                }
            }
            if card.waiting.more > 0 {
                Text("+\(card.waiting.more) more").font(face()).foregroundColor(pixel ? secondary : tertiary)
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
            Text(meter.label).font(face()).foregroundColor(secondary).lineLimit(1).frame(width: labelWidth, alignment: .leading)
            Group {
                if pixel {
                    PixelBar(share: meter.shown, color: warn ? palette.color("Critical") : identity, blocks: 14)
                } else {
                    ClassicBar(share: meter.shown, color: warn ? palette.color("Critical") : identity, track: identity.opacity(0.2))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(meter.value).font(face()).fontWeight(warn && !pixel ? .semibold : .regular)
                .foregroundColor(warn ? palette.color("Critical") : secondary)
                .frame(width: valueWidth, alignment: .trailing)
        }
    }

    private func tokensRow(_ tokens: TokensModel) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(tokens.label).font(face()).foregroundColor(secondary).frame(width: labelWidth, alignment: .leading)
            if tokens.todayNumber.isEmpty {
                Text(tokens.today).font(face()).foregroundColor(tertiary)
            } else {
                VStack(alignment: .leading, spacing: 2) {
                    HStack { Text("Today").font(face()).foregroundColor(secondary); Spacer(); Text(tokens.todayNumber).font(face()).foregroundColor(primary) }
                    HStack { Text("This month").font(face()).foregroundColor(secondary); Spacer(); Text(tokens.monthNumber).font(face()).foregroundColor(primary) }
                }
            }
        }
    }
}

/// The card: sections in columns that fit the room, in the skin's panel.
struct CardView: View {
    let model: CardsModel
    let palette: Palette
    /// Columns of section indexes, chosen by CardPresenter.
    let columns: [[Int]]
    var tailY: CGFloat?
    var rightEdge = true

    private var pixel: Bool { palette.skin == .pixel }
    static let reach: CGFloat = 9

    var column: CGFloat { (pixel ? 272 : 264) - 32 }

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            ForEach(Array(columns.enumerated()), id: \.offset) { position, indexes in
                if position > 0 {
                    divider.frame(maxHeight: .infinity).padding(.horizontal, 16)
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
        .padding(.horizontal, 16).padding(.vertical, pixel ? 14 : 13)
        .background(plate)
        .padding(.leading, rightEdge ? 0 : CardView.reach).padding(.trailing, rightEdge ? CardView.reach : 0)
        .padding(pixel ? 4 : 20)
        .overlay(alignment: .topLeading) { tail }
    }

    @ViewBuilder private var plate: some View {
        if pixel {
            PixelPlateBackground(palette: palette)
        } else {
            RoundedRectangle(cornerRadius: 8)
                .fill(palette.color("Paper"))
                .overlay(RoundedRectangle(cornerRadius: 8).stroke(palette.hairline, lineWidth: 1))
                .shadow(color: .black.opacity(palette.dark ? 0.45 : 0.18), radius: 10, x: 0, y: 4)
        }
    }

    private var rule: some View {
        Rectangle().fill(palette.color(pixel ? "LineSoft" : "LineSoft")).frame(height: pixel ? 2 : 1)
    }

    private var divider: some View {
        Rectangle().fill(palette.color("LineSoft")).frame(width: pixel ? 2 : 1)
    }

    /// The pointer toward the character the card belongs to.
    @ViewBuilder private var tail: some View {
        if let tailY {
            let inset: CGFloat = pixel ? 4 : 20
            GeometryReader { proxy in
                let x = rightEdge ? proxy.size.width - inset - CardView.reach : inset
                let y = tailY + inset
                if pixel {
                    PixelPointer(palette: palette, rightEdge: rightEdge).position(x: x + (rightEdge ? 4 : 5), y: y)
                } else {
                    Triangle(rightEdge: rightEdge).fill(palette.color("Paper"))
                        .frame(width: 9, height: 14).position(x: x + (rightEdge ? 4.5 : 4.5), y: y)
                }
            }
        }
    }
}

struct Triangle: Shape {
    let rightEdge: Bool
    func path(in rect: CGRect) -> Path {
        var path = Path()
        if rightEdge {
            path.move(to: CGPoint(x: rect.minX - 0.5, y: rect.minY)); path.addLine(to: CGPoint(x: rect.maxX, y: rect.midY))
            path.addLine(to: CGPoint(x: rect.minX - 0.5, y: rect.maxY))
        } else {
            path.move(to: CGPoint(x: rect.maxX + 0.5, y: rect.minY)); path.addLine(to: CGPoint(x: rect.minX, y: rect.midY))
            path.addLine(to: CGPoint(x: rect.maxX + 0.5, y: rect.maxY))
        }
        path.closeSubpath()
        return path
    }
}

/// Three cells against the edge, one beyond them: a pixel arrowhead.
struct PixelPointer: View {
    let palette: Palette
    let rightEdge: Bool

    var body: some View {
        let c: CGFloat = 2
        ZStack {
            Rectangle().fill(palette.color("Line")).frame(width: c, height: 4 * c)
            Rectangle().fill(palette.color("Line")).frame(width: c, height: 2 * c).offset(x: rightEdge ? c : -c)
            Rectangle().fill(palette.color("Paper")).frame(width: c, height: 2 * c).offset(x: rightEdge ? -c / 2 : c / 2).opacity(0)
        }
        .frame(width: 3 * c, height: 4 * c)
    }
}
