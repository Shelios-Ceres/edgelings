using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentIsland.Core;

namespace AgentIsland.Usage;

/// Claude's login in the macOS Keychain, reached through /usr/bin/security the
/// way the upstream Mac app does (its ClaudeCredentials.swift). Claude Code 2.x
/// files its account under "Claude Code-credentials-<8 hex>" (a per-config
/// suffix), while a web login and older CLIs use the plain name. The suffixed
/// prefix is also where per-config MCP OAuth caches live, so a name alone
/// does not identify the login: an item counts only when it parses as
/// claudeAiOauth with a token pair, and the freshest expiresAt wins when
/// several accounts have left items behind.
///
/// The rotated refresh token must go back to the SAME item it was read from
/// (Claude Code reads its own item on its next refresh), keeping every other
/// field of the stored JSON. Nothing here prints or logs a token or any other
/// part of an item's secret; failures are reported as outcomes only.
///
/// `security` can block for ever behind a keychain authorization dialog, so
/// each call has a deadline and is killed at it. The write passes the JSON as
/// an argument (-w), as upstream does: briefly visible to the same user's
/// processes, behind the same trust boundary as the keychain itself.
internal sealed class ClaudeKeychain
{
    public const string BaseService = "Claude Code-credentials";

    internal sealed record Item(string Service, string Account, JsonObject Root)
    {
        public JsonObject? Oauth => Root["claudeAiOauth"] as JsonObject;
        public string? AccessToken => Text(Oauth, "accessToken");
        public string? RefreshToken => Text(Oauth, "refreshToken");
        public string? SubscriptionType => Text(Oauth, "subscriptionType");
        public double ExpiresAt => Oauth?["expiresAt"] is JsonValue value && value.TryGetValue<double>(out var at) ? at : 0;

        private static string? Text(JsonObject? node, string key) =>
            node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    internal enum WriteResult
    {
        Written,
        /// The item no longer holds the refresh token that was sent.
        Superseded,
        Failed,
    }

    /// Runs `security` with these arguments; stdout on exit 0, else null.
    internal delegate string? SecurityRunner(IReadOnlyList<string> arguments);

    private readonly SecurityRunner _run;
    private readonly string _baseService;
    private readonly string? _keychain;
    private string? _remembered;

    /// The real thing: the login keychain, the real service names.
    public static ClaudeKeychain Default { get; } = new(RunSecurity, BaseService, null);

    /// runner and baseService are seams for tests; keychain names a keychain
    /// file other than the default (a throwaway one in tests).
    public ClaudeKeychain(SecurityRunner runner, string baseService, string? keychain)
    {
        _run = runner;
        _baseService = baseService;
        _keychain = keychain;
    }

    /// The freshest Claude login the keychain holds. Fast path is the item
    /// that answered last, then the plain name; only when both miss does the
    /// suffixed-item discovery run (metadata only: the dump has no secrets).
    public Item? Read()
    {
        var fast = new List<string>();
        if (_remembered is not null) fast.Add(_remembered);
        if (!fast.Contains(_baseService)) fast.Add(_baseService);
        var hit = Freshest(fast);
        if (hit is null)
        {
            hit = Freshest(DiscoverSuffixed().Where(name => !fast.Contains(name)).ToList());
        }
        if (hit is not null) _remembered = hit.Service;
        return hit;
    }

    /// Forget the remembered item so the next read discovers again.
    public void Forget() => _remembered = null;

    /// Writes the rotated tokens into the item they came from, only while it
    /// still holds the refresh token that was sent (the Grok rule): if Claude
    /// Code refreshed first, or the user signed in again as another account,
    /// the item is left alone.
    public WriteResult WriteBack(string service, string accessToken, string refreshToken, long expiresAtMs, string sentRefreshToken)
    {
        try
        {
            if (ReadItem(service) is not { } current) return WriteResult.Failed;
            if (!string.Equals(current.RefreshToken, sentRefreshToken, StringComparison.Ordinal)) return WriteResult.Superseded;
            var root = current.Root;
            if (root["claudeAiOauth"] is not JsonObject oauth) return WriteResult.Superseded;
            oauth["accessToken"] = accessToken;
            oauth["refreshToken"] = refreshToken;
            oauth["expiresAt"] = expiresAtMs;
            var arguments = new List<string>
            {
                "add-generic-password", "-U", "-s", service, "-a", current.Account, "-w", root.ToJsonString(),
            };
            return _run(WithKeychain(arguments)) is null ? WriteResult.Failed : WriteResult.Written;
        }
        catch (Exception)
        {
            return WriteResult.Failed;
        }
    }

    /// Cheap local change signal for the re-auth poll: the item's modification
    /// date. A fresh login may mint a new suffixed item, so on a miss the
    /// discovery runs; the stamp turning from null to something is the login.
    public string? ModificationStamp()
    {
        var candidates = new List<string>();
        if (_remembered is not null) candidates.Add(_remembered);
        if (!candidates.Contains(_baseService)) candidates.Add(_baseService);
        foreach (var service in candidates)
        {
            if (ReadMetadata("mdat", service) is { } stamp) return stamp;
        }
        foreach (var service in DiscoverSuffixed().Where(name => !candidates.Contains(name)))
        {
            if (ReadMetadata("mdat", service) is { } stamp) return stamp;
        }
        return null;
    }

    private Item? Freshest(IEnumerable<string> services)
    {
        Item? best = null;
        foreach (var service in services)
        {
            if (ReadItem(service) is { } item && (best is null || item.ExpiresAt > best.ExpiresAt)) best = item;
        }
        return best;
    }

    private Item? ReadItem(string service)
    {
        if (ReadMetadata("acct", service) is not { } account) return null;
        var output = _run(WithKeychain(new List<string> { "find-generic-password", "-s", service, "-a", account, "-w" }));
        if (output is null) return null;
        try
        {
            if (JsonNode.Parse(Decode(output.Trim())) is not JsonObject root) return null;
            var item = new Item(service, account, root);
            return string.IsNullOrEmpty(item.AccessToken) || string.IsNullOrEmpty(item.RefreshToken) ? null : item;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// `security -w` prints a secret that isn't plain printable text as hex.
    internal static string Decode(string output)
    {
        if (output.Length > 0 && output[0] == '{') return output;
        if (output.Length == 0 || output.Length % 2 != 0 || !output.All(Uri.IsHexDigit)) return output;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(output));
        }
        catch (FormatException)
        {
            return output;
        }
    }

    private List<string> DiscoverSuffixed()
    {
        var output = _run(WithKeychain(new List<string> { "dump-keychain" }));
        return output is null ? new List<string>() : ParseServiceNames(output, _baseService);
    }

    /// Line-parse of `security dump-keychain` metadata: the unique service
    /// names carrying the suffixed prefix, sorted ordinally.
    internal static List<string> ParseServiceNames(string dump, string baseService)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        const string marker = "\"svce\"<blob>=\"";
        foreach (var line in dump.Split('\n'))
        {
            var at = line.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) continue;
            var rest = line.AsSpan(at + marker.Length);
            var end = rest.IndexOf('"');
            if (end < 0) continue;
            var name = rest[..end].ToString();
            if (name.StartsWith(baseService + "-", StringComparison.Ordinal)) names.Add(name);
        }
        return names.ToList();
    }

    /// One attribute (acct, mdat) from the item's metadata listing: the last
    /// quoted value after the equals sign, or null for a missing line or <NULL>.
    private string? ReadMetadata(string key, string service)
    {
        var output = _run(WithKeychain(new List<string> { "find-generic-password", "-s", service }));
        return output is null ? null : MetadataValue(output, key);
    }

    internal static string? MetadataValue(string listing, string key)
    {
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("\"" + key + "\"", StringComparison.Ordinal)) continue;
            var equals = line.IndexOf('=');
            if (equals < 0) return null;
            var right = line[(equals + 1)..];
            var close = right.LastIndexOf('"');
            if (close < 0) return null;
            var open = right.LastIndexOf('"', close - 1);
            if (open < 0) return null;
            var value = right[(open + 1)..close];
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    private IReadOnlyList<string> WithKeychain(List<string> arguments)
    {
        if (_keychain is not null) arguments.Add(_keychain);
        return arguments;
    }

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DumpDeadline = TimeSpan.FromSeconds(20);

    internal static string? RunSecurity(IReadOnlyList<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/security")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            // Both pipes are drained while waiting: a full pipe would stall the child.
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            var deadline = arguments.Count > 0 && arguments[0] == "dump-keychain" ? DumpDeadline : Deadline;
            if (!process.WaitForExit(deadline))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            if (process.ExitCode != 0 || !output.Wait(TimeSpan.FromSeconds(2))) return null;
            return output.Result;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
