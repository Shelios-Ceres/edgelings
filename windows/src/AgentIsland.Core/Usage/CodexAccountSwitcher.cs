using System.Text;
using System.Text.Json;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// One-click Codex account switching, ported from the macOS
/// CodexAccountSwitcher (owner call, 2026-08-08).
///
/// Codex keeps exactly one signed-in account in %USERPROFILE%\.codex\auth.json.
/// This parks a COPY of each account's credentials beside it and swaps the
/// live file on demand — the same thing a person does by hand with `move`,
/// minus the mistakes. Two modes:
///   - MANUAL — the user picks from the settings menu, the file swaps.
///   - AUTO (opt-in, off by default) — when the live account's window reads
///     100%, rotate to the next parked account. Modeled on the open-source
///     codex-auto pool tools, but keyed off the real /wham/usage percentages
///     instead of scraping terminal output for failure text, so it never
///     guesses. UsageStore owns the trigger (it sees every fresh reading);
///     this type owns policy + the swap.
public static class CodexAccountSwitcher
{
    /// A parked account: the label the user gave it plus its stored file.
    /// Email is read out of the stored credentials so the picker can show who
    /// a login belongs to; the token itself never leaves the file.
    public sealed record Account(string Label, string Path, string? Email);

    private const string AutoSwitchKey = "codexAutoSwitchWhenExhausted";
    private const int MaxLabelLength = 40;

    /// Reserved DOS device names still resolve as devices in any directory and
    /// with any extension, so a label of "CON" would send the credentials to
    /// the console instead of "CON.json".
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static IReadOnlyList<Account> Accounts() => Accounts(IslandPaths.CodexHome);

    internal static IReadOnlyList<Account> Accounts(string home)
    {
        try
        {
            var store = Path.Combine(home, "agentisland-accounts");
            if (!Directory.Exists(store)) return Array.Empty<Account>();
            return Directory.EnumerateFiles(store)
                .Where(path => Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .Select(path => new Account(
                    Path.GetFileNameWithoutExtension(path), path, EmailInFile(path)))
                .OrderBy(account => account.Label, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not list parked Codex accounts: {error.Message}");
            return Array.Empty<Account>();
        }
    }

    /// Match the account and user, not tokens that Codex refreshes in place.
    public static string? ActiveLabel() => ActiveLabel(IslandPaths.CodexHome);

    internal static string? ActiveLabel(string home)
    {
        if (ReadBytesOrNull(Path.Combine(home, "auth.json")) is not { } live) return null;
        foreach (var parked in Accounts(home))
        {
            if (ReadBytesOrNull(parked.Path) is { } stored && SameAccount(stored, live))
            {
                return parked.Label;
            }
        }
        return null;
    }

    /// Park the CURRENT login under `label`, so it can be switched back to.
    public static bool ParkCurrent(string? label) => ParkCurrent(label, IslandPaths.CodexHome);

    internal static bool ParkCurrent(string? label, string home)
    {
        if (ReadBytesOrNull(Path.Combine(home, "auth.json")) is not { } live) return false;
        return Park(label, live, home);
    }

    private static bool Park(string? label, byte[] data, string home)
    {
        if (StoredPath(Sanitize(label), home) is not { } target) return false;
        var store = Path.Combine(home, "agentisland-accounts");
        try
        {
            Directory.CreateDirectory(store);
            Harden(store);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not create the Codex account store: {error.Message}");
            return false;
        }
        return WriteAtomic(target, data);
    }

    /// Save the exact outgoing credentials before replacing the live login.
    public static bool Activate(Account account) => Activate(account, IslandPaths.CodexHome);

    internal static bool Activate(Account account, string home)
    {
        if (InStore(account.Path, home) is not { } target || ReadBytesOrNull(target) is not { } data)
            return false;
        var livePath = Path.Combine(home, "auth.json");
        if (!TryRead(livePath, out var live)) return false;
        if (live is not null)
        {
            // Selecting the current account must never restore its older tokens.
            if (SameAccount(live, data)) return true;
            var matching = Accounts(home).Where(parked =>
                ReadBytesOrNull(parked.Path) is { } stored && SameAccount(stored, live)).ToList();
            if (matching.Count == 0)
            {
                if (!Park("previous-" + Guid.NewGuid().ToString("N")[..30], live, home)) return false;
            }
            else
            {
                // Refresh every alias, so a later rotation cannot select a stale copy.
                foreach (var parked in matching)
                    if (!SameBytes(ReadBytesOrNull(parked.Path), live) && !WriteAtomic(parked.Path, live)) return false;
            }
        }
        try
        {
            Directory.CreateDirectory(home);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not prepare the Codex home directory: {error.Message}");
            return false;
        }
        // A CLI refresh or another switch while backing up invalidates this snapshot.
        if (!TryRead(livePath, out var latest) || !SameBytes(live, latest)) return false;
        return WriteAtomic(livePath, data);
    }

    public static bool Forget(Account account)
    {
        // Delete only inside our own store: the Account comes from a caller,
        // and this is the one code path that removes a credentials file.
        if (InStore(account.Path, IslandPaths.CodexHome) is not { } target) return false;
        try
        {
            if (!File.Exists(target)) return false;
            File.Delete(target);
            return true;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not remove parked Codex account: {error.Message}");
            return false;
        }
    }

    // MARK: - Auto-switch (opt-in)

    /// Off by default: silently swapping logins surprises anyone who did not
    /// ask for it, and a mid-session file swap is visible to a running Codex
    /// CLI. The owner flips it on once and forgets it.
    public static bool AutoSwitchEnabled
    {
        get => Preferences.Get<bool?>(AutoSwitchKey) ?? false;
        set => Preferences.Set(AutoSwitchKey, value);
    }

    /// The next account worth rotating to: parked, not the live one, not yet
    /// tried this exhaustion episode. Stable label order, so rotation walks
    /// the pool deterministically instead of ping-ponging between two logins.
    public static Account? RotationCandidate(IReadOnlySet<string> tried) =>
        RotationCandidate(tried, IslandPaths.CodexHome);

    internal static Account? RotationCandidate(IReadOnlySet<string> tried, string home)
    {
        if (!TryRead(Path.Combine(home, "auth.json"), out var live)) return null;
        var accounts = Accounts(home);
        var attempted = accounts.Where(account => tried.Contains(account.Label))
            .Select(account => ReadBytesOrNull(account.Path)).OfType<byte[]>().ToList();
        foreach (var account in accounts)
        {
            if (tried.Contains(account.Label)) continue;
            if (ReadBytesOrNull(account.Path) is not { } stored) continue;
            if (live is not null && SameAccount(stored, live)) continue;
            if (attempted.Any(previous => SameAccount(stored, previous))) continue;
            return account;
        }
        return null;
    }

    // MARK: - Helpers (unit-tested)

    internal sealed record Identity(string AccountId, string? UserId);

    internal static bool SameAccount(byte[] first, byte[] second) =>
        first.SequenceEqual(second) ||
        (IdentityIn(first) is { } identity && identity == IdentityIn(second));

    private static Identity? IdentityIn(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            return IdentityIn(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    internal static Identity? IdentityIn(JsonElement root)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tokens", out var tokens) ||
                tokens.ValueKind != JsonValueKind.Object) return null;
            var account = StringIn(tokens, "account_id");
            string? user = null, subject = null;
            foreach (var key in new[] { "id_token", "access_token" })
            {
                if (StringIn(tokens, key)?.Split('.') is not { Length: 3 } parts) continue;
                try
                {
                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                    payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
                    using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
                    subject ??= StringIn(claims.RootElement, "sub");
                    if (claims.RootElement.TryGetProperty("https://api.openai.com/auth", out var auth))
                    {
                        account ??= StringIn(auth, "chatgpt_account_id");
                        user ??= StringIn(auth, "chatgpt_user_id");
                    }
                }
                catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException) { }
            }
            // Email alone is not an identity: one user may have several workspaces.
            return account is null ? null : new Identity(account, user ?? subject);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    private static string? StringIn(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static bool SameBytes(byte[]? first, byte[]? second) =>
        first is null ? second is null : second is not null && first.SequenceEqual(second);

    /// Labels become filenames: keep them to safe characters so a stray "\" or
    /// ".." can never escape the store directory. Strips rather than rejects —
    /// a label is cosmetic, unlike a session id that would reach a shell — and
    /// every write re-checks the resolved path anyway.
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var kept = new StringBuilder(Math.Min(raw.Length, MaxLabelLength));
        foreach (var ch in raw)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_' && ch != ' ') continue;
            kept.Append(ch);
            if (kept.Length >= MaxLabelLength) break;
        }
        // Trim AFTER the cap, not only before it: Win32 silently drops a
        // trailing space from a filename, so "work " and "work" would fight
        // over one file while reporting two different labels.
        var clean = kept.ToString().Trim();
        if (clean.Length == 0) return string.Empty;
        return ReservedNames.Contains(clean.ToUpperInvariant()) ? clean + "_" : clean;
    }

    /// Codex writes the account email in fields next to the token block; read
    /// the first email-shaped string. Never returns — and never logs — the
    /// token itself.
    internal static string? EmailInJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = property.Value.GetString();
                    if (text is not null && text.Contains('@') && !text.Contains(' ')) return text;
                    break;
                case JsonValueKind.Object:
                    if (EmailInJson(property.Value) is { } nested) return nested;
                    break;
            }
        }
        return null;
    }

    private static string? EmailInFile(string path)
    {
        try
        {
            if (ReadBytesOrNull(path) is not { } data) return null;
            using var document = JsonDocument.Parse(data);
            return EmailInJson(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    /// Where a sanitized label lives, or null when the label is empty.
    private static string? StoredPath(string cleanLabel, string home) =>
        cleanLabel.Length == 0 ? null : InStore(Path.Combine(home, "agentisland-accounts", cleanLabel + ".json"), home);

    /// Resolve a path and confirm it sits DIRECTLY inside the store directory.
    /// Sanitize already makes an escape impossible for labels; this is the
    /// check that keeps the guarantee if Sanitize ever loosens, and the only
    /// gate a caller-supplied Account path passes through.
    private static string? InStore(string candidate, string home)
    {
        try
        {
            var target = Path.GetFullPath(candidate);
            if (Path.GetDirectoryName(target) is not { } parent) return null;
            return string.Equals(
                parent.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(Path.Combine(home, "agentisland-accounts")).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
                ? target
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// Cooperate with a CLI that keeps auth.json open; activation separately
    /// distinguishes a missing login from one that cannot safely be read.
    private static byte[]? ReadBytesOrNull(string path)
        => TryRead(path, out var data) ? data : null;

    private static bool TryRead(string path, out byte[]? data)
    {
        data = null;
        try
        {
            using var stream = SharedFile.Open(path);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            data = memory.ToArray();
            return true;
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch
        {
            return false;
        }
    }

    /// Temp file + rename, so a crash mid-write never leaves a truncated
    /// auth.json: the live file carries either the old login or the new one.
    /// The rename is retried because a running Codex CLI can hold a brief read
    /// lock on it.
    private static bool WriteAtomic(string path, byte[] data)
    {
        // Per-write temp name: Windows denies a second opener while the first
        // holds the handle, so two Agent Island instances auto-rotating at the
        // same exhaustion boundary would fail each other's write on a shared
        // ".tmp". The suffix keeps it out of Accounts(), which lists ".json".
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tmp, data);
            Harden(tmp);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return true;
                }
                // Replacing a file another handle holds without share-delete
                // throws UnauthorizedAccessException, not IOException.
                catch (Exception moveError) when (attempt < 4 && moveError is IOException or UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine(
                $"AgentIsland: could not write Codex credentials: {error.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }

    /// Windows has no chmod 600. The store sits inside the user profile, so it
    /// inherits that profile's DACL — exactly the protection auth.json itself
    /// gets — and we additionally keep it out of the content index so a copy of
    /// a live token never lands in the Windows Search database.
    private static void Harden(string path)
    {
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.NotContentIndexed);
        }
        catch
        {
            // Best effort: a FAT volume or a policy-managed index setting is no
            // reason to refuse the switch.
        }
    }
}
