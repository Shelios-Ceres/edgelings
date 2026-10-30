// Usage & cost: totals across agents, then one card per agent for the chosen
// period with a chart, its hero number and its models.
import SwiftUI

enum UsagePeriod: String, CaseIterable, Identifiable {
    case today, week, month
    var id: String { rawValue }
    var key: String {
        switch self {
        case .today: return "Today"
        case .week: return "7 days"
        case .month: return "This month"
        }
    }
}

struct UsagePage: View {
    @ObservedObject var store: SettingsStore
    @State private var period: UsagePeriod = .today

    var body: some View {
        let kit = Kit(store: store)
        let palette = store.palette
        PageTitle(store: store, key: "Usage & cost")
        PageIntro(store: store, text: store.t("Estimated at API list prices from this computer's session logs — not a bill."))
        HStack(spacing: 8) {
            ForEach(UsagePeriod.allCases) { option in
                Button(action: { period = option }) {
                    Text(store.t(option.key)).font(kit.body())
                        .foregroundColor(period == option ? palette.color("OnAccent") : palette.color("Text"))
                        .padding(.horizontal, 12).padding(.vertical, 5)
                        .background(RoundedRectangle(cornerRadius: kit.pixel ? 0 : 6).fill(period == option ? palette.color("Accent") : palette.color("Field")))
                }
                .buttonStyle(.plain)
            }
            Spacer()
            Text(store.usage?.scannedAgo.map { "\($0)" } ?? store.t("Not scanned yet")).font(kit.body(12)).foregroundColor(palette.color("Text3"))
            KitButton(store: store, title: store.t("Scan now")) { store.send("rescan", [:]) }
        }
        if let usage = store.usage {
            totals(usage, kit: kit, palette: palette)
            ForEach(usage.agents.filter { $0.shown }) { agent in
                UsageAgentCard(store: store, agent: agent, period: period)
            }
            Text(usage.zone).font(kit.body(11)).foregroundColor(palette.color("Text3"))
        }
        quotaSection
    }

    private func slice(_ part: UsagePart) -> PeriodUsage {
        switch period {
        case .today: return part.today
        case .week: return part.week
        case .month: return part.month
        }
    }

    @ViewBuilder private func totals(_ usage: UsageModel, kit: Kit, palette: Palette) -> some View {
        let shown = usage.agents.filter { $0.shown }
        let parts = shown.flatMap(\.parts)
        let spend = parts.filter(\.dollars).reduce(0) { $0 + slice($1).dollars }
        let tokens = parts.reduce(Int64(0)) { $0 + slice($1).tokens }
        let scanned = parts.contains(where: \.scanned)
        let busiest = shown.max { a, b in a.parts.reduce(Int64(0)) { $0 + slice($1).tokens } < b.parts.reduce(Int64(0)) { $0 + slice($1).tokens } }
        let busiestTokens = busiest?.parts.reduce(Int64(0)) { $0 + slice($1).tokens } ?? 0
        HStack(alignment: .top, spacing: 10) {
            stat(store.t("Estimated cost"), scanned ? money(spend) : "—", store.t("At API list prices"), kit: kit, palette: palette)
            stat(store.t("Tokens"), scanned ? compact(tokens) : "—", store.quota.tokenModeLabel, kit: kit, palette: palette)
            stat(store.t("Busiest agent"), busiestTokens > 0 ? (busiest?.name ?? "—") : "—",
                 busiestTokens > 0 ? store.t("{0} tokens", compact(busiestTokens)) : store.t(scanned ? "No activity yet" : "Not scanned yet"), kit: kit, palette: palette)
        }
    }

    private func stat(_ label: String, _ value: String, _ caption: String, kit: Kit, palette: Palette) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(label).font(kit.body(12)).foregroundColor(palette.color("Text2"))
            Text(value).font(kit.pixel ? PixelFace.font(22) : .system(size: 22, weight: .semibold)).foregroundColor(palette.color("Text"))
            Text(caption).font(kit.body(11)).foregroundColor(palette.color("Text3")).fixedSize(horizontal: false, vertical: true)
        }
        .padding(14).frame(maxWidth: .infinity, alignment: .leading)
        .modifier(CardSurface(store: store))
    }

    private var quotaSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionLabel(store: store, text: store.t("Quota"))
            if let quota = store.settings?.quota {
                SettingRow(store: store, title: store.t("Refresh interval"), subtitle: store.t("Providers rate-limit their usage endpoints, so nothing shorter than five minutes is offered")) {
                    KitMenu(store: store, options: quota.presets.map { store.t("Every {0} minutes", $0 / 60) }, selected: quota.presets.firstIndex(of: quota.interval) ?? 0) { index in
                        store.send("set", ["interval": quota.presets[index]])
                    }
                }
                SettingRow(store: store, title: store.t("Quota shows"), subtitle: store.t("How the bars and percentages read on the drawer's cards and in Settings")) {
                    KitMenu(store: store, options: [store.t("Used"), store.t("Remaining")], selected: quota.showsRemaining ? 1 : 0) { index in
                        store.send("set", ["showsRemaining": index == 1])
                    }
                }
                SectionLabel(store: store, text: store.t("Tokens"))
                SettingRow(store: store, title: store.t("Token counting"), subtitle: store.t("Excludes cache reads; new input written to cache still counts. History is kept.")) {
                    KitMenu(store: store, options: [store.t("All tokens (including cache)"), store.t("Input + output (excluding cache reads)")], selected: quota.tokenMode == "all" ? 0 : 1) { index in
                        store.send("set", ["tokenMode": index == 0 ? "all" : "billable"])
                    }
                }
            }
        }
    }
}

extension SettingsStore {
    var quota: QuotaPrefs { settings?.quota ?? QuotaPrefs(interval: 300, presets: [300, 900, 1800], showsRemaining: false, tokenMode: "all", tokenModeLabel: "") }
}

func compact(_ tokens: Int64) -> String {
    let value = Double(tokens)
    switch value {
    case 1_000_000_000...: return String(format: "%.1fB", value / 1_000_000_000)
    case 1_000_000...: return String(format: "%.1fM", value / 1_000_000)
    case 10_000...: return String(format: "%.0fK", value / 1_000)
    case 1_000...: return String(format: "%.1fK", value / 1_000)
    default: return "\(tokens)"
    }
}

func money(_ dollars: Double) -> String { String(format: "$%.2f", dollars) }

struct UsageAgentCard: View {
    @ObservedObject var store: SettingsStore
    let agent: UsageAgent
    let period: UsagePeriod

    var body: some View {
        let kit = Kit(store: store)
        let palette = store.palette
        let identity = palette.readable(agent.accent)
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                if kit.pixel { Rectangle().fill(Color(hex: agent.accent)).frame(width: 8, height: 8) }
                else { Circle().fill(Color(hex: agent.accent)).frame(width: 16, height: 16) }
                Text(agent.name).font(kit.strong(14)).foregroundColor(palette.color("Text"))
                Spacer()
            }
            ForEach(agent.parts) { part in
                partView(part, identity: identity, kit: kit, palette: palette)
            }
        }
        .padding(16).frame(maxWidth: .infinity, alignment: .leading)
        .modifier(CardSurface(store: store))
    }

    private func slice(_ part: UsagePart) -> PeriodUsage {
        switch period {
        case .today: return part.today
        case .week: return part.week
        case .month: return part.month
        }
    }

    private func series(_ part: UsagePart) -> [Double] {
        switch period {
        case .today: return part.dollars ? differences(part.hourlyDollars) : part.hourly
        case .week: return part.days.map { part.dollars ? $0.dollars : Double($0.tokens) }
        case .month: return part.monthDays.map { part.dollars ? $0.dollars : Double($0.tokens) }
        }
    }

    /// Today's dollars come cumulative by hour; the chart wants each hour's own.
    private func differences(_ cumulative: [Double]) -> [Double] {
        var previous = 0.0
        return cumulative.map { value in defer { previous = value }; return max(0, value - previous) }
    }

    @ViewBuilder private func partView(_ part: UsagePart, identity: Color, kit: Kit, palette: Palette) -> some View {
        let data = slice(part)
        VStack(alignment: .leading, spacing: 8) {
            if agent.parts.count > 1 { Text(part.name).font(kit.body(12)).foregroundColor(palette.color("Text2")) }
            if !part.scanned {
                Text(store.t("Not scanned yet")).font(kit.body(12)).foregroundColor(palette.color("Text3"))
            } else {
                HStack(alignment: .firstTextBaseline) {
                    Text(part.dollars ? money(data.dollars) : compact(data.tokens))
                        .font(kit.pixel ? PixelFace.font(22) : .system(size: 22, weight: .semibold)).foregroundColor(palette.color("Text"))
                    Spacer()
                    Text(part.dollars ? store.t("{0} tokens", compact(data.tokens)) : "\(compact(data.tokens)) tokens · billed by Cursor's own plan")
                        .font(kit.body(12)).foregroundColor(palette.color("Text2"))
                }
                let values = series(part)
                if values.contains(where: { $0 > 0 }) {
                    BarChart(values: values, color: identity, track: identity.opacity(0.15), pixel: kit.pixel)
                }
                if data.models.isEmpty {
                    Text(store.t("No sessions in this period")).font(kit.body(12)).foregroundColor(palette.color("Text3"))
                } else {
                    ForEach(Array(data.models.enumerated()), id: \.offset) { _, model in
                        HStack {
                            Text(model.name).font(kit.body(12)).foregroundColor(palette.color("Text")).lineLimit(1)
                            Spacer()
                            let price = !part.dollars ? "" : model.unpriced ? "  ·  " + store.t("no price") : "  ·  " + money(model.dollars)
                            Text(compact(model.tokens) + price).font(kit.body(12)).foregroundColor(palette.color("Text2"))
                        }
                    }
                }
            }
        }
    }
}
