using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Cost;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;

namespace AgentIsland.Bridge;

/// What the Mac Settings window reads and changes: the Windows pages' data
/// (Agents, Drawer, Usage & cost, General), as JSON. Every text is chosen
/// here, in the app's language (English when the Pixel skin asks for it),
/// so the Swift side holds no copy of the strings.
public static class SettingsApi
{
    private static IDisposable? English(JsonElement request) =>
        request.TryGetProperty("english", out var value) && value.ValueKind == JsonValueKind.True ? L10n.English() : null;

    /// {"method":"tr","keys":["Agents",…],"english":false}: each key in the app's language.
    public static string Tr(JsonElement request)
    {
        using var english = English(request);
        return Json.Write(json =>
        {
            json.WriteStartObject("strings");
            if (request.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var key in keys.EnumerateArray())
                {
                    if (key.GetString() is { } text) json.WriteString(text, L10n.Tr(text));
                }
            }
            json.WriteEndObject();
            json.WriteBoolean("chinese", L10n.IsChinese);
        });
    }

    public static string Settings(JsonElement request)
    {
        using var english = English(request);
        var store = DrawerStore.Shared;
        var visibility = ProviderVisibilityStore.Shared;
        var lineup = DrawerVisibility.Lineup(store.Order, visibility.IsShown);
        return Json.Write(json =>
        {
            json.WriteStartObject("drawer");
            json.WriteString("theme", store.Theme == CharacterTheme.Pixel ? "pixel" : "classic");
            json.WriteBoolean("speech", store.SpeechBubbles);
            json.WriteString("side", store.Side == DrawerSide.Right ? "right" : "left");
            json.WriteNumber("offset", store.Offset);
            json.WriteBoolean("locked", store.Locked);
            json.WriteNumber("fps", store.FrameRate);
            json.WriteBoolean("saveBattery", store.SaveBattery);
            json.WriteString("display", IslandTargetDisplayStore.Shared.Choice);
            json.WriteEndObject();

            json.WriteStartObject("quota");
            json.WriteNumber("interval", RefreshIntervalStore.Shared.Seconds);
            json.WriteStartArray("presets");
            foreach (var preset in RefreshIntervalStore.Presets) json.WriteNumberValue(preset);
            json.WriteEndArray();
            json.WriteBoolean("showsRemaining", QuotaDisplayModeStore.Shared.ShowsRemaining);
            json.WriteString("tokenMode", TokenCountModeStore.Shared.Mode == TokenCountMode.Billable ? "billable" : "all");
            json.WriteString("tokenModeLabel", TokenDisplayText.ModeLabel(TokenCountModeStore.Shared.Mode));
            json.WriteEndObject();

            json.WriteString("language", AppLanguageStore.Load() switch
            {
                L10n.Language.English => "en",
                L10n.Language.SimplifiedChinese => "zh",
                _ => "auto",
            });
            json.WriteBoolean("chinese", L10n.IsChinese);

            json.WriteStartArray("agents");
            foreach (var (provider, shown) in lineup)
            {
                var report = AgentReports.For(provider);
                json.WriteStartObject();
                json.WriteString("id", provider.RawValue());
                json.WriteString("name", ProviderIdentity.DisplayName(provider));
                json.WriteBoolean("shown", shown);
                json.WriteBoolean("detected", report.Detected);
                if (report.Plan is { } plan) json.WriteString("plan", plan);
                if (report.Account is { } account) json.WriteString("account", account);
                json.WriteString("status", report.Status);
                if (report.Warning is { } warning) json.WriteString("warning", warning);
                if (report.Note is { } note) json.WriteString("note", note);
                json.WriteString("signIn", report.SignIn.ToString().ToLowerInvariant());
                json.WriteString("signInHint", report.SignInHint);
                json.WriteNumber("accent", ProviderIdentity.Accent(provider));
                var worn = LookCatalog.Of(store.Theme, provider);
                json.WriteNumber("swatch", worn.Swatch);
                json.WriteBoolean("defaultLook", worn == LookCatalog.For(store.Theme, provider)[0]);
                json.WriteStartArray("meters");
                foreach (var meter in report.Meters)
                {
                    json.WriteStartObject();
                    json.WriteString("label", meter.Label);
                    json.WriteString("title", MeterTitle(meter.Label));
                    json.WriteString("value", meter.Value);
                    if (meter.Shown is { } fraction) json.WriteNumber("shown", fraction);
                    if (meter.Used is { } used) json.WriteNumber("used", used);
                    if (meter.Resets is { } resets) json.WriteString("resets", resets);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                if (report.Tokens is { } tokens)
                {
                    json.WriteStartObject("tokens");
                    json.WriteString("label", tokens.Label);
                    json.WriteString("today", tokens.Today);
                    json.WriteString("todayNumber", tokens.TodayNumber);
                    json.WriteString("month", tokens.Month);
                    json.WriteString("monthNumber", tokens.MonthNumber);
                    json.WriteEndObject();
                }
                json.WriteStartObject("look");
                json.WriteString("pixel", LookCatalog.Of(CharacterTheme.Pixel, provider).Id);
                json.WriteString("classic", LookCatalog.Of(CharacterTheme.Classic, provider).Id);
                json.WriteEndObject();
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartObject("hooks");
            json.WriteBoolean("claude", ClaudeHooks.IsInstalled());
            json.WriteBoolean("grok", GrokHooks.IsInstalled());
            json.WriteBoolean("grokConflict", GrokHooks.Conflicts());
            json.WriteEndObject();

            json.WriteStartObject("codex");
            json.WriteBoolean("autoSwitch", CodexAccountSwitcher.AutoSwitchEnabled);
            if (CodexAccountSwitcher.ActiveLabel() is { } active) json.WriteString("active", active);
            json.WriteStartArray("accounts");
            foreach (var account in CodexAccountSwitcher.Accounts())
            {
                json.WriteStartObject();
                json.WriteString("label", account.Label);
                if (account.Email is { } email) json.WriteString("email", email);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteBoolean("kimiMail", true);
        });
    }

    /// What a window is called on the Agents page (AgentSection.MeterTitle).
    private static string MeterTitle(string label) => label switch
    {
        _ when label == L10n.Tr("5h") => L10n.Tr("5-hour window"),
        _ when label == L10n.Tr("Week") => L10n.Tr("Weekly window"),
        _ when label == L10n.Tr("24h") => L10n.Tr("Daily window"),
        _ when label == L10n.Tr("30d") => L10n.Tr("Billing cycle"),
        _ when label == L10n.Tr("Month") => L10n.Tr("Monthly window"),
        _ => label,
    };

    /// One change: {"method":"set", …}. Each present field is applied.
    ///   language: "auto" | "en" | "zh"; interval: 300 | 900 | 1800; showsRemaining: bool;
    ///   tokenMode: "all" | "billable"
    public static string Set(JsonElement request)
    {
        foreach (var property in request.EnumerateObject())
        {
            switch (property.Name)
            {
                case "language":
                    var language = property.Value.GetString() switch
                    {
                        "en" => L10n.Language.English,
                        "zh" => L10n.Language.SimplifiedChinese,
                        _ => L10n.Language.Auto,
                    };
                    AppLanguageStore.Save(language);
                    L10n.Current = language;
                    break;
                case "interval": RefreshIntervalStore.Shared.Seconds = property.Value.GetInt32(); break;
                case "showsRemaining": QuotaDisplayModeStore.Shared.ShowsRemaining = property.Value.GetBoolean(); break;
                case "tokenMode": TokenCountModeStore.Shared.Mode = property.Value.GetString() == "billable" ? TokenCountMode.Billable : TokenCountMode.All; break;
            }
        }
        return Json.Write(json => json.WriteBoolean("ok", true));
    }

    /// {"method":"agent","action":…,"id":"claude", …}
    ///   signIn · refresh · hook (on) · codexSwitch (label) · codexSave (label) · codexForget (label) · codexAuto (on)
    public static string Agent(JsonElement request)
    {
        var action = request.TryGetProperty("action", out var a) ? a.GetString() : null;
        var provider = request.TryGetProperty("id", out var id) ? DisplayProviders.Parse(id.GetString()) : null;
        var label = request.TryGetProperty("label", out var l) ? l.GetString() : null;
        var on = request.TryGetProperty("on", out var o) && o.ValueKind == JsonValueKind.True;
        string? error = null;
        var done = true;
        switch (action)
        {
            case "signIn": done = provider is { } who && AgentReports.StartSignIn(who); break;
            case "refresh": if (provider is { } p) AgentReports.RefreshNow(p); break;
            case "hook":
                error = provider == DisplayProvider.Claude ? ClaudeHooks.SetInstalled(on)
                    : provider == DisplayProvider.Grok ? GrokHooks.SetInstalled(on)
                    : "no hook for this agent";
                done = error is null;
                break;
            case "codexSwitch":
                if (CodexAccountSwitcher.Accounts().FirstOrDefault(account => account.Label == label) is { } target)
                {
                    done = CodexAccountSwitcher.Activate(target);
                    if (done)
                    {
                        UsageStore.Shared.CodexAutoSwitched = null;
                        UsageStore.Shared.RefreshAfterCodexSwitch();
                    }
                }
                else done = false;
                break;
            case "codexSave":
                var parked = CodexAccountSwitcher.ParkCurrent(string.IsNullOrWhiteSpace(label) ? null : label);
                done = parked == CodexAccountSwitcher.ParkResult.Saved;
                if (!done) error = parked == CodexAccountSwitcher.ParkResult.NameTaken ? "name taken" : "failed";
                break;
            case "codexForget":
                if (CodexAccountSwitcher.Accounts().FirstOrDefault(account => account.Label == label) is { } gone) CodexAccountSwitcher.Forget(gone);
                break;
            case "codexAuto": CodexAccountSwitcher.AutoSwitchEnabled = on; break;
            default: return Json.Write(json => json.WriteString("error", $"unknown action '{action}'"));
        }
        return Json.Write(json =>
        {
            json.WriteBoolean("ok", done);
            if (error is not null) json.WriteString("error", error);
        });
    }

    /// The Usage & cost page, one entry per agent that has a character:
    /// today, the last seven days and the month, with their models and charts.
    public static string Usage(JsonElement request)
    {
        using var english = English(request);
        var billable = TokenCountModeStore.Shared.Mode == TokenCountMode.Billable;
        var visibility = ProviderVisibilityStore.Shared;
        var order = DrawerStore.Shared.Order.Where(p => p.HasCharacter()).ToList();
        return Json.Write(json =>
        {
            json.WriteBoolean("billable", billable);
            if (CostStore.Shared.LastUpdated is { } updated)
            {
                json.WriteString("scannedAgo", Formatting.RelativeAgo(DateTimeOffset.Now - updated, L10n.IsChinese));
            }
            json.WriteString("zone", TokenDisplayText.TimeZoneLabel(DateTimeOffset.Now));
            json.WriteStartArray("agents");
            foreach (var provider in order)
            {
                // Grok's rollup carries Cursor's tokens beside it, billed by Cursor's own plan.
                var members = provider == DisplayProvider.Grok ? new[] { DisplayProvider.Grok, DisplayProvider.Cursor } : new[] { provider };
                json.WriteStartObject();
                json.WriteString("id", provider.RawValue());
                json.WriteString("name", ProviderIdentity.DisplayName(provider));
                json.WriteBoolean("shown", visibility.IsShown(provider));
                json.WriteNumber("accent", ProviderIdentity.Accent(provider));
                var wornLook = LookCatalog.Of(DrawerStore.Shared.Theme, provider);
                json.WriteNumber("swatch", wornLook.Swatch);
                json.WriteBoolean("defaultLook", wornLook == LookCatalog.For(DrawerStore.Shared.Theme, provider)[0]);
                json.WriteString("look", wornLook.Id);
                json.WriteStartArray("parts");
                foreach (var member in members)
                {
                    var summary = CostStore.Shared.Summary(member);
                    json.WriteStartObject();
                    json.WriteString("id", member.RawValue());
                    json.WriteString("name", ProviderIdentity.DisplayName(member));
                    json.WriteBoolean("dollars", member != DisplayProvider.Cursor);
                    json.WriteBoolean("scanned", summary.AsOf is not null);
                    WritePeriod(json, "today", summary.TodayDollars, summary.TodayTokens, summary.TodayBillableTokens,
                        summary.TodayModels.Count > 0 ? summary.TodayModels : summary.RecentModels, member, summary, billable);
                    WritePeriod(json, "week", summary.LastSevenDays.Dollars,
                        summary.LastSevenDays.DailyTokens.Sum(d => d.Tokens), summary.LastSevenDays.DailyTokens.Sum(d => d.BillableTokens),
                        summary.LastSevenDays.ByModel, member, summary, billable);
                    WritePeriod(json, "month", summary.MonthDollars, summary.MonthTokens, summary.MonthBillableTokens,
                        summary.MonthModels, member, summary, billable);
                    json.WriteStartArray("hourly");
                    for (var hour = 0; hour < summary.TodayHourlyTokens.Count; hour++)
                    {
                        json.WriteNumberValue(billable ? summary.TodayHourlyBillableTokens[hour] : summary.TodayHourlyTokens[hour]);
                    }
                    json.WriteEndArray();
                    json.WriteStartArray("hourlyDollars");
                    foreach (var cumulative in summary.TodayCumulativeDollars) json.WriteNumberValue(cumulative);
                    json.WriteEndArray();
                    json.WriteStartArray("days");
                    foreach (var day in summary.LastSevenDays.DailyTokens)
                    {
                        json.WriteStartObject();
                        json.WriteString("date", day.DayStart.ToString("MM-dd"));
                        json.WriteNumber("tokens", billable ? day.BillableTokens : day.Tokens);
                        json.WriteNumber("dollars", day.Dollars);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteStartArray("monthDays");
                    var now = DateTimeOffset.Now;
                    foreach (var day in summary.DailyHistory.Where(d => d.DayStart.Year == now.Year && d.DayStart.Month == now.Month))
                    {
                        json.WriteStartObject();
                        json.WriteString("date", day.DayStart.ToString("MM-dd"));
                        json.WriteNumber("tokens", billable ? day.BillableTokens : day.Tokens);
                        json.WriteNumber("dollars", day.Dollars);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
        });
    }

    private static void WritePeriod(System.Text.Json.Utf8JsonWriter json, string name, double dollars, long tokens, long billableTokens,
        IReadOnlyList<ModelSpend> models, DisplayProvider provider, ProviderCostSummary summary, bool billable)
    {
        json.WriteStartObject(name);
        json.WriteNumber("dollars", dollars);
        json.WriteNumber("tokens", billable ? billableTokens : tokens);
        json.WriteStartArray("models");
        var ranked = ModelSpend.Ranked(models, billable);
        // Kimi Code lists K3 and later, and every connected provider's models; its K2 rows stay in the totals.
        var listed = provider == DisplayProvider.Kimi
            ? ranked.Where(m => !KimiLogReader.IsK2(m.Model, summary.ModelNames.GetValueOrDefault(m.Model))).ToList()
            : ranked.Take(3).ToList();
        foreach (var model in listed)
        {
            json.WriteStartObject();
            json.WriteString("name", summary.NameOf(model.Model));
            json.WriteNumber("tokens", billable ? model.BillableTokens : model.Tokens);
            json.WriteNumber("dollars", model.Dollars);
            json.WriteBoolean("unpriced", model.Dollars == 0 && model.Tokens > 0 && !Pricing.IsKnown(model.Model));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// A formatted number the way Windows shows it.
    public static string Format(JsonElement request)
    {
        var kind = request.TryGetProperty("kind", out var k) ? k.GetString() : "";
        var value = request.TryGetProperty("value", out var v) ? v.GetDouble() : 0;
        return Json.Write(json => json.WriteString("text", kind == "money" ? Formatting.Money(value) : Formatting.CompactTokens((long)value)));
    }

    /// Scan the logs now.
    public static string Rescan()
    {
        CostStore.Shared.Refresh();
        return Json.Write(json => json.WriteBoolean("ok", true));
    }
}
