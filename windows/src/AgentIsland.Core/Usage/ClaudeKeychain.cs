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
/// Limen 只读这里：不写钥匙串，也不刷新登录。读取只为描述登录状态和判断终端登录有没有完成。
/// Nothing here prints or logs a token or any other
/// part of an item's secret; failures are reported as outcomes only.
///
/// `security` can block for ever behind a keychain authorization dialog, so
/// each read has a deadline.
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

        public override string ToString() => $"Item {{ Service = {Service}, secret redacted }}";

        private static string? Text(JsonObject? node, string key) =>
            node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    /// Runs `security` with these arguments; stdout on exit 0, else null.
    internal delegate string? SecurityRunner(IReadOnlyList<string> arguments);

    /// The same, with the exit code: 44 (errSecItemNotFound) is the one code that
    /// says "no such item"; a timeout, another code or a failed start says nothing
    /// about whether the item exists.
    internal delegate string? SecurityRunnerWithCode(IReadOnlyList<string> arguments, out int exitCode);

    internal const int ItemNotFound = 44;

    private readonly SecurityRunnerWithCode _runWithCode;
    // Per thread: a read is one synchronous call, and two callers must not see each other's outcome.
    [ThreadStatic] private static bool _readFailed;
    private readonly string _baseService;
    private readonly string? _keychain;
    private string? _remembered;

    /// The real thing: the login keychain, the real service names.
    public static ClaudeKeychain Default { get; } = new(RunSecurityWithCode, BaseService, null);

    /// runner and baseService are seams for tests; keychain names a keychain
    /// file other than the default (a throwaway one in tests).
    public ClaudeKeychain(SecurityRunner runner, string baseService, string? keychain)
        : this(WithCode(runner), baseService, keychain)
    {
    }

    public ClaudeKeychain(SecurityRunnerWithCode runner, string baseService, string? keychain)
    {
        _runWithCode = runner;
        _baseService = baseService;
        _keychain = keychain;
    }

    /// A runner that cannot report a code never proves an item is missing.
    private static SecurityRunnerWithCode WithCode(SecurityRunner runner) => (IReadOnlyList<string> arguments, out int exitCode) =>
    {
        var output = runner(arguments);
        exitCode = output is null ? -1 : 0;
        return output;
    };

    private string? _run(IReadOnlyList<string> arguments)
    {
        var output = _runWithCode(arguments, out var exitCode);
        if (output is null && exitCode != ItemNotFound) _readFailed = true;
        return output;
    }

    /// True when the last Read could not tell "no login" from "could not ask".
    public bool LastReadFailed => _readFailed;

    /// The freshest Claude login the keychain holds. Fast path is the item
    /// that answered last, then the plain name; only when both miss does the
    /// suffixed-item discovery run (metadata only: the dump has no secrets).
    public Item? Read()
    {
        _readFailed = false;
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

    internal static string? RunSecurity(IReadOnlyList<string> arguments) => RunSecurityWithCode(arguments, out _);

    internal static string? RunSecurityWithCode(IReadOnlyList<string> arguments, out int exitCode)
    {
        exitCode = -1;
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
            exitCode = process.ExitCode;
            if (process.ExitCode != 0 || !output.Wait(TimeSpan.FromSeconds(2))) return null;
            return output.Result;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
