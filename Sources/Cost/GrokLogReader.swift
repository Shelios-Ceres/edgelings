import Foundation

/// Walks the local Grok CLI session logs and emits a TokenEvent for every
/// completed turn. Grok is the one provider that SHIPS ITS OWN COST: each
/// `turn_completed` update carries a `usage` object with `costUsdTicks`
/// (xAI API ticks: 1 USD = 10^10 ticks, USD × 1e-10, so 158652000 ticks = $0.0158652),
/// so the event's `selfReportedCostUSD` is set directly and OVERRIDES the
/// Pricing table downstream — Grok dollars are never guessed from a rate snapshot.
///
///   ~/.grok/sessions/<url-encoded cwd>/<uuid>/updates.jsonl
///
/// Each usage line is one turn: `params.update.usage` holds top-level totals
/// plus a `modelUsage` map keyed by model id. When more than one model ran in
/// a turn we emit one event per model from ITS OWN sub-figures (never the
/// top-level, which would double count); a turn with no modelUsage falls back
/// to a single "grok" event from the top-level figures.
///
/// Per-file parse results are memoized in `grok-parse-cache.v1.json` keyed by
/// (path, mtime, size), same as the Claude/Codex readers — the cached shape is
/// this reader's own `CachedEvent`, so the self-reported dollars survive the
/// round-trip.
enum GrokLogReader {
    static func scan(lookbackDays: Int = 30) -> [TokenEvent] {
        let cutoff = Date().addingTimeInterval(-Double(lookbackDays) * 86400)
        var seen = Set<String>()
        var out: [TokenEvent] = []

        LogParseCache.walk(
            roots: [sessionsRoot()],
            cutoff: cutoff,
            cacheFilename: "grok-parse-cache.v1.json",
            cacheVersion: cacheVersion,
            fileFilter: { $0.lastPathComponent == "updates.jsonl" },
            parse: parseFile(at:),
            emit: { (ev: CachedEvent) in
                guard ev.timestamp >= cutoff else { return }
                if !ev.dedupKey.isEmpty {
                    if seen.contains(ev.dedupKey) { return }
                    seen.insert(ev.dedupKey)
                }
                out.append(TokenEvent(
                    provider: .grok,
                    timestamp: ev.timestamp,
                    model: ev.model,
                    inputTokens: ev.inputTokens,
                    outputTokens: ev.outputTokens,
                    cacheCreationTokens: 0,
                    cacheReadTokens: ev.cacheReadTokens,
                    selfReportedCostUSD: ev.selfReportedCostUSD
                ))
            }
        )
        return out
    }

    private static func sessionsRoot() -> URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".grok/sessions", isDirectory: true)
    }

    /// Parse one updates.jsonl end-to-end. Non-usage updates (tool calls,
    /// message chunks, retries) are skipped; a `usage` byte-marker pre-filter
    /// keeps the JSON parse off the vast majority of lines.
    private static func parseFile(at url: URL) -> [CachedEvent] {
        var out: [CachedEvent] = []
        LogParseCache.streamLines(at: url, maxLineBytes: maxUsefulLineBytes) { lineData in
            guard lineData.range(of: usageMarker) != nil else { return }
            appendEvents(from: lineData, into: &out)
        }
        return out
    }

    private static func appendEvents(from lineData: Data, into out: inout [CachedEvent]) {
        guard let raw = try? JSONSerialization.jsonObject(with: lineData) as? [String: Any],
              let params = raw["params"] as? [String: Any],
              let update = params["update"] as? [String: Any],
              let usage = update["usage"] as? [String: Any]
        else { return }

        let tsSeconds = raw["timestamp"] as? Int ?? 0
        let timestamp = tsSeconds > 0
            ? Date(timeIntervalSince1970: Double(tsSeconds))
            : Date.distantPast
        let promptID = update["prompt_id"] as? String ?? ""

        // One event per model when the turn broke usage down by model;
        // otherwise a single "grok" event from the top-level figures.
        if let modelUsage = usage["modelUsage"] as? [String: Any], !modelUsage.isEmpty {
            for (model, value) in modelUsage {
                guard let sub = value as? [String: Any] else { continue }
                appendModel(sub, model: model, timestamp: timestamp, promptID: promptID, into: &out)
            }
        } else {
            appendModel(usage, model: "grok", timestamp: timestamp, promptID: promptID, into: &out)
        }
    }

    private static func appendModel(
        _ fields: [String: Any],
        model: String,
        timestamp: Date,
        promptID: String,
        into out: inout [CachedEvent]
    ) {
        let input = fields["inputTokens"] as? Int ?? 0
        let output = fields["outputTokens"] as? Int ?? 0
        let cachedRead = fields["cachedReadTokens"] as? Int ?? 0
        if input == 0 && output == 0 && cachedRead == 0 { return }

        // Grok self-reports cost in xAI ticks (1 USD = 10^10 ticks, USD × 1e-10).
        // Absent ticks stay nil rather than being priced from a table Grok has no entry in.
        let cost = (fields["costUsdTicks"] as? Int).map { Double($0) * 1e-10 }

        // Dedup key pins the model too: a multi-model turn shares one prompt_id
        // across its per-model events, so keying on prompt_id alone would drop
        // every model after the first. Empty prompt_id ⇒ no dedup.
        let dedupKey = promptID.isEmpty ? "" : "\(promptID)|\(model)"

        out.append(CachedEvent(
            timestamp: timestamp,
            model: model,
            inputTokens: input,
            outputTokens: output,
            cacheReadTokens: cachedRead,
            selfReportedCostUSD: cost,
            dedupKey: dedupKey
        ))
    }

    // MARK: - Line pre-filter

    /// Usage payloads are well under 1 KB; the big lines in an updates stream
    /// are tool-call results, which never carry a `usage` object. Skipping
    /// them at the reader keeps peak memory flat (the Codex 1 MiB trick).
    private static let maxUsefulLineBytes = 1 << 20
    private static let usageMarker = Data("usage".utf8)

    // MARK: - Per-file cache

    private static let cacheVersion = 1

    private struct CachedEvent: Codable {
        let timestamp: Date
        let model: String
        let inputTokens: Int
        let outputTokens: Int
        let cacheReadTokens: Int
        let selfReportedCostUSD: Double?
        let dedupKey: String
    }
}
