using AgentIsland.Core;

namespace AgentIsland.Cost;

/// One billable model call reconstructed from a local log line.
public sealed record TokenEvent(
    TriggerTool Provider,
    DateTimeOffset Timestamp,
    string Model,
    long InputTokens,
    long OutputTokens,
    long CacheCreationTokens,
    long CacheReadTokens,
    double? SelfReportedCostUSD = null)
{
    /// ccusage parity: everything that crossed the wire.
    public long WireTokens => InputTokens + OutputTokens + CacheCreationTokens + CacheReadTokens;

    /// Excludes cache reads, but includes new input written to cache.
    /// The legacy name is kept for callers and the saved display preference.
    public long BillableTokens => InputTokens + CacheCreationTokens + OutputTokens;

    public string? ResponseKey { get; init; }
    public bool IsFinalUsage { get; init; }

    /// Dollar cost for this event. A provider that ships its own figure
    /// (Grok's costUsdTicks) sets <see cref="SelfReportedCostUSD"/>, and when
    /// present it OVERRIDES the Pricing-table computation — the provider's own
    /// number wins. Table-priced providers (Claude, Codex, Cursor) leave it
    /// null and fall back to the embedded rate snapshot.
    public double Dollars => SelfReportedCostUSD ?? Pricing.Cost(this);
}
