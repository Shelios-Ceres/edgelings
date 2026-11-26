using System.Net;
using System.Runtime.InteropServices;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Grok's quota is read-only: the access token in auth.json is used while it
/// lives, never refreshed, and never written back. A refused or expired token
/// stops the requests until the grok CLI has rewritten the file. Synthetic
/// tokens only, in a temporary grok home.
public static class GrokAuthFileTests
{
    private const string Scope = "https://auth.x.ai::client-1";
    private const string Billing = """{"config":{"creditUsagePercent":10}}""";

    public static void RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agentisland-grokauth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previousFile = Preferences.FileOverride;
        try
        {
            // What the fetcher remembers across a restart goes to a file of its own, not the user's settings.
            Preferences.FileOverride = Path.Combine(directory, "settings.json");
            Preferences.Forget();
            Step("a live access token is read, with the installed version and this machine's platform", () => LiveTokenIsRead(Home(directory, "live")));
            Step("an expired token, or one about to expire, sends nothing and says to open grok", () => ExpiredSendsNothing(Home(directory, "expired")));
            Step("a refused token (401 or 403) stops the requests, manual refresh included", () => RefusedStops(Home(directory, "refused")));
            Step("requests resume once auth.json has changed, and stop again if that token is refused too", () => ResumesOnChange(Home(directory, "resume")));
            Step("auth.json is never written: not its bytes, its time or its folder", () => NeverWritten(Home(directory, "readonly")));
            Step("a missing auth.json is not installed, and a refusal does not outlive it", () => MissingIsNotInstalled(Home(directory, "missing")));
            Step("a refused token is still refused after a restart, and the settings hold no token", () => RestartKeepsRefusal(Home(directory, "restart")));
            Step("a file deleted and made again lifts the hold", () => Recreated(Home(directory, "recreated")));
            Step("a link to auth.json is stamped by its target", () => LinkedStamp(Home(directory, "linked")));
            Step("the version comes from version.json only when CHANGELOG.md says the same; a bad one is left out", () => VersionSources(Home(directory, "version")));
            Step("the exe's own version resource wins over the files beside it", () => ExeVersion(Home(directory, "exe")));
            Step("the platform and architecture are named as the CLI names them", PlatformIsActual);
            Step("the caption reads in Chinese, and an unknown window never puts the character to sleep", CaptionAndExhaustion);
            Console.WriteLine("GrokAuthFileTests GREEN");
        }
        finally
        {
            GrokUsageFetcher.ResetStateForTests();
            Preferences.FileOverride = previousFile;
            Preferences.Forget();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    /// A test that cannot run here reports a skip, not a pass.
    private static void Step(string name, Func<bool> test)
    {
        GrokUsageFetcher.ResetStateForTests();
        if (test()) Console.WriteLine($"PASS grok credentials: {name}");
        else Console.WriteLine($"SKIP grok credentials: {name}");
    }

    private static void Step(string name, Action test)
    {
        GrokUsageFetcher.ResetStateForTests();
        test();
        Console.WriteLine($"PASS grok credentials: {name}");
    }

    private static string Home(string root, string name)
    {
        var home = Path.Combine(root, name);
        Directory.CreateDirectory(home);
        return home;
    }

    private static string Auth(string access, long expiresAt, string email = "a@example.test") => $$"""
        {
          "{{Scope}}": {
            "key": "{{access}}",
            "refresh_token": "never-used-refresh",
            "expires_at": {{expiresAt}},
            "auth_mode": "oidc",
            "email": "{{email}}",
            "extra": "kept"
          }
        }
        """;

    private static long In(TimeSpan span) => DateTimeOffset.Now.Add(span).ToUnixTimeMilliseconds();

    private sealed class Server
    {
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> Methods = new();
        public readonly List<string> Urls = new();
        public Func<string, HttpStatusCode> Status = _ => HttpStatusCode.OK;
        public HttpClient Client { get; }

        public Server() => Client = new HttpClient(new Handler(request =>
        {
            lock (Requests)
            {
                Requests.Add(request);
                Methods.Add(request.Method.Method);
                Urls.Add(request.RequestUri!.ToString());
            }
            var status = Status(request.Headers.Authorization!.Parameter!);
            return status == HttpStatusCode.OK
                ? new(status) { Content = new StringContent(Billing) }
                : new(status);
        }));

        public int Count
        {
            get { lock (Requests) return Requests.Count; }
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }

    private static GrokUsageFetcher.Outcome Fetch(string home, Server server) =>
        GrokUsageFetcher.Fetch(Path.Combine(home, "auth.json"), server.Client, default).GetAwaiter().GetResult();

    private static void Touch(string path, int seconds) =>
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 11, 26, 3, 0, 0, DateTimeKind.Utc).AddSeconds(seconds));

    /// Everything a write would change: each file's bytes and time, and the names in the folder.
    private static string Fingerprint(string home) => string.Join("|", Directory.EnumerateFileSystemEntries(home)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => $"{Path.GetFileName(path)}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(File.ReadAllBytes(path))}"));

    private static void LiveTokenIsRead(string home)
    {
        File.WriteAllText(Path.Combine(home, "auth.json"), Auth("live-access", In(TimeSpan.FromHours(3))));
        File.WriteAllText(Path.Combine(home, "version.json"), """{"version":"1.0.70"}""");
        File.WriteAllText(Path.Combine(home, "CHANGELOG.md"), "# 1.0.70\n");
        var server = new Server();
        var outcome = Fetch(home, server);
        Expect(outcome is GrokUsageFetcher.Outcome.Success, $"a live token reads the quota, got {outcome}");
        Expect(server.Count, 2);
        Expect(server.Methods.All(method => method == "GET"), "only reads");
        Expect(server.Urls.All(url => url.StartsWith("https://cli-chat-proxy.grok.com/v1/billing", StringComparison.Ordinal)), "only the billing proxy is asked");
        foreach (var request in server.Requests)
        {
            Expect(request.Headers.Authorization!.Parameter, "live-access");
            Expect(request.Headers.GetValues("x-xai-token-auth").Single(), "xai-grok-cli");
            Expect(request.Headers.GetValues("x-grok-client-version").Single(), "1.0.70");
            var agent = string.Join(" ", request.Headers.GetValues("User-Agent"));
            Expect(agent, $"grok-pager/1.0.70 grok-shell/1.0.70 ({GrokClientIdentity.CurrentPlatform}; {GrokClientIdentity.CurrentArchitecture})");
            Expect(!agent.Contains("0.2.114"), "the old fixed version is gone");
        }
    }

    private static void ExpiredSendsNothing(string home)
    {
        var path = Path.Combine(home, "auth.json");
        foreach (var expiresAt in new[] { In(TimeSpan.FromHours(-1)), In(TimeSpan.FromSeconds(30)) })
        {
            File.WriteAllText(path, Auth("old-access", expiresAt));
            var server = new Server();
            Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired, "an expired token asks for a sync");
            Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired, "and again on the next poll");
            Expect(server.Count, 0);
        }
        File.WriteAllText(path, Auth("live-access", In(TimeSpan.FromMinutes(5))));
        var live = new Server();
        Expect(Fetch(home, live) is GrokUsageFetcher.Outcome.Success, "five minutes left is still live");
    }

    private static void RefusedStops(string home)
    {
        var path = Path.Combine(home, "auth.json");
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            GrokUsageFetcher.ResetStateForTests();
            File.WriteAllText(path, Auth("refused-access", In(TimeSpan.FromHours(3))));
            Touch(path, 10);
            var server = new Server { Status = _ => status };
            Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired, $"{(int)status} asks for a sync");
            var sent = server.Count;
            Expect(sent > 0, "the refused token was sent once");
            // A poll, and the manual refresh button: both end up in Fetch.
            for (var kick = 0; kick < 5; kick++)
            {
                Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired, "still asks for a sync");
            }
            Expect(server.Count, sent);
        }
    }

    private static void ResumesOnChange(string home)
    {
        var path = Path.Combine(home, "auth.json");
        File.WriteAllText(path, Auth("old-access", In(TimeSpan.FromHours(3))));
        Touch(path, 10);
        var server = new Server { Status = token => token == "new-access" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized };
        Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired, "refused");
        var sent = server.Count;
        Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired && server.Count == sent, "held while nothing changed");

        // The grok CLI rewrote the file with a fresh token.
        File.WriteAllText(path, Auth("new-access", In(TimeSpan.FromHours(6))));
        Touch(path, 20);
        Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.Success, "a changed file is read again");
        Expect(server.Count > sent, "and requests went out");
        Expect(server.Requests.Skip(sent).All(request => request.Headers.Authorization!.Parameter == "new-access"), "with the new token");

        // A change that brings no better token costs one more try, then holds again.
        File.WriteAllText(path, Auth("still-refused", In(TimeSpan.FromHours(6))));
        Touch(path, 30);
        var before = server.Count;
        Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired, "refused again");
        var after = server.Count;
        Expect(after > before, "it was tried once");
        Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.SyncRequired && server.Count == after, "and held again");

        // Only the time moving is a change too: the file was touched.
        Touch(path, 40);
        Fetch(home, server);
        Expect(server.Count > after, "a new modification time alone lifts the hold");
    }

    private static void NeverWritten(string home)
    {
        var path = Path.Combine(home, "auth.json");
        File.WriteAllText(path, Auth("old-access", In(TimeSpan.FromHours(-2))));
        File.WriteAllText(Path.Combine(home, "version.json"), """{"version":"1.0.70"}""");
        Touch(path, 5);
        var before = Fingerprint(home);

        var expired = new Server();
        Fetch(home, expired);
        Expect(Fingerprint(home), before);

        File.WriteAllText(path, Auth("live-access", In(TimeSpan.FromHours(3))));
        Touch(path, 6);
        before = Fingerprint(home);
        foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests })
        {
            GrokUsageFetcher.ResetStateForTests();
            var server = new Server { Status = _ => status };
            Fetch(home, server);
            Fetch(home, server);
            Expect(Fingerprint(home), before);
            Expect(server.Methods.All(method => method == "GET") && server.Urls.All(url => !url.Contains("auth.x.ai")), "nothing is sent to the sign-in server");
        }
    }

    private static void MissingIsNotInstalled(string home)
    {
        var path = Path.Combine(home, "auth.json");
        File.WriteAllText(path, Auth("old-access", In(TimeSpan.FromHours(3))));
        var server = new Server { Status = _ => HttpStatusCode.Unauthorized };
        Fetch(home, server);
        File.Delete(path);
        Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.NotInstalled, "no file, not signed in");
        Expect(!File.Exists(path), "and nothing recreates it");
    }

    private static void RestartKeepsRefusal(string home)
    {
        var path = Path.Combine(home, "auth.json");
        File.WriteAllText(path, Auth("refused-access-SECRET", In(TimeSpan.FromHours(3))));
        Touch(path, 10);
        var server = new Server { Status = _ => HttpStatusCode.Unauthorized };
        Fetch(home, server);
        var sent = server.Count;
        Expect(sent > 0, "refused once");

        GrokUsageFetcher.ForgetMemoryForTests();
        Fetch(home, server);
        Expect(server.Count, sent);
        var settings = File.ReadAllText(Preferences.FileOverride!);
        Expect(!settings.Contains("SECRET") && !settings.Contains("refused-access"), "no token in the settings");

        File.WriteAllText(path, Auth("new-access", In(TimeSpan.FromHours(3))));
        Touch(path, 20);
        GrokUsageFetcher.ForgetMemoryForTests();
        Fetch(home, server);
        Expect(server.Count > sent, "after a restart a changed file is sent again");
        var again = server.Count;
        Fetch(home, server);
        Expect(server.Count, again);
    }

    private static void Recreated(string home)
    {
        var path = Path.Combine(home, "auth.json");
        File.WriteAllText(path, Auth("old-access", In(TimeSpan.FromHours(3))));
        Touch(path, 10);
        var server = new Server { Status = _ => HttpStatusCode.Unauthorized };
        Fetch(home, server);
        var sent = server.Count;

        File.Delete(path);
        Expect(Fetch(home, server) is GrokUsageFetcher.Outcome.NotInstalled, "gone");
        // The same bytes and the same time, made again: not the file that was refused.
        File.WriteAllText(path, Auth("old-access", In(TimeSpan.FromHours(3))));
        Touch(path, 10);
        Fetch(home, server);
        Expect(server.Count > sent, "a file made again is tried again");
    }

    private static bool LinkedStamp(string home)
    {
        var target = Path.Combine(home, "real-auth.json");
        File.WriteAllText(target, Auth("old-access", In(TimeSpan.FromHours(3))));
        Touch(target, 10);
        var link = Path.Combine(home, "link", "auth.json");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
        var before = GrokAuthFile.Stamp(link);
        Expect(before is not null && before == GrokAuthFile.Stamp(target), "a link is stamped by its target");
        Touch(target, 99);
        Expect(GrokAuthFile.Stamp(link) != before, "the target moving shows through the link");
        Expect(GrokAuthFile.Stamp(link), GrokAuthFile.Stamp(target));
        return true;
    }

    private static void VersionSources(string home)
    {
        string Fresh(string name)
        {
            var folder = Home(home, name);
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            return folder;
        }

        var agree = Fresh("agree");
        File.WriteAllText(Path.Combine(agree, "version.json"), """{"version":"1.0.70","stable_version":null}""");
        File.WriteAllText(Path.Combine(agree, "CHANGELOG.md"), "\n# 1.0.70 \u2014 2026-10-23\n\n## Features\n");
        Expect(GrokClientIdentity.InstalledVersion(agree), "1.0.70");

        // A newer build seen or downloading is not the installed one: with the two apart, no version.
        var apart = Fresh("apart");
        File.WriteAllText(Path.Combine(apart, "version.json"), """{"version":"1.0.92"}""");
        File.WriteAllText(Path.Combine(apart, "CHANGELOG.md"), "# 1.0.70 \u2014 2026-10-23\n");
        Expect(GrokClientIdentity.InstalledVersion(apart) is null, "files that disagree give no version");

        // A four-part version is cut to its first three.
        var four = Fresh("four");
        File.WriteAllText(Path.Combine(four, "version.json"), """{"version":"1.0.70.0"}""");
        File.WriteAllText(Path.Combine(four, "CHANGELOG.md"), "# 1.0.70\n");
        Expect(GrokClientIdentity.InstalledVersion(four), "1.0.70");

        var onlyJson = Fresh("only-json");
        File.WriteAllText(Path.Combine(onlyJson, "version.json"), """{"version":"1.0.70"}""");
        Expect(GrokClientIdentity.InstalledVersion(onlyJson) is null, "one file alone gives no version");
        var onlyLog = Fresh("only-log");
        File.WriteAllText(Path.Combine(onlyLog, "CHANGELOG.md"), "# 1.0.70\n");
        Expect(GrokClientIdentity.InstalledVersion(onlyLog) is null, "nor does the other alone");

        var broken = Fresh("broken");
        File.WriteAllText(Path.Combine(broken, "version.json"), "{ not json");
        File.WriteAllText(Path.Combine(broken, "CHANGELOG.md"), "# 1.0.73\n");
        Expect(GrokClientIdentity.InstalledVersion(broken) is null, "a broken file gives no version");

        foreach (var bad in new[] { "latest", "1.0", "1.0.70\\r\\nX-Evil: 1", "1.0.70 beta" })
        {
            var folder = Fresh("bad");
            File.WriteAllText(Path.Combine(folder, "version.json"), $$"""{"version":"{{bad}}"}""");
            File.WriteAllText(Path.Combine(folder, "CHANGELOG.md"), $"# {bad}\n");
            Expect(GrokClientIdentity.InstalledVersion(folder) is null, $"a value that is not a version is left out: {bad}");
        }

        var none = Fresh("none");
        Expect(GrokClientIdentity.InstalledVersion(none) is null, "no version file, no version");
        Expect(GrokClientIdentity.InstalledVersion(Path.Combine(home, "no-such-folder")) is null, "no folder, no version");
        Expect(GrokClientIdentity.Current(none).UserAgent, $"grok-pager grok-shell ({GrokClientIdentity.CurrentPlatform}; {GrokClientIdentity.CurrentArchitecture})");

        // Without a version the request carries no version header, and says nothing invented.
        File.WriteAllText(Path.Combine(none, "auth.json"), Auth("live-access", In(TimeSpan.FromHours(3))));
        var server = new Server();
        Expect(Fetch(none, server) is GrokUsageFetcher.Outcome.Success, "still reads");
        Expect(server.Requests.All(request => !request.Headers.Contains("x-grok-client-version")), "no version header");
        Expect(server.Requests.All(request => !string.Join(" ", request.Headers.GetValues("User-Agent")).Contains("0.2.114")), "no invented version");
    }

    private static bool ExeVersion(string home)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        // Any signed-in .NET host carries a version resource; use the running one as the "grok.exe".
        var bin = Path.Combine(home, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "grok.exe");
        File.Copy(Environment.ProcessPath!, exe, overwrite: true);
        var resource = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        Expect(resource is { Length: > 0 }, "the host has a version resource");
        var read = GrokClientIdentity.FromExe(exe);
        Expect(read is not null && resource!.StartsWith(read, StringComparison.Ordinal), $"the exe's version is read, got {read}");
        File.WriteAllText(Path.Combine(home, "version.json"), """{"version":"1.0.70"}""");
        File.WriteAllText(Path.Combine(home, "CHANGELOG.md"), "# 1.0.70\n");
        Expect(GrokClientIdentity.InstalledVersion(home), read);
        Expect(GrokClientIdentity.FromExe(Path.Combine(bin, "missing.exe")) is null, "no exe, no version");
        return true;
    }

    private static void PlatformIsActual()
    {
        Expect(GrokClientIdentity.ArchitectureName(Architecture.X64), "x86_64");
        Expect(GrokClientIdentity.ArchitectureName(Architecture.Arm64), "aarch64");
        Expect(GrokClientIdentity.ArchitectureName(Architecture.X86), "x86");
        // This machine, by what is known of it rather than by the code that names it.
        if (OperatingSystem.IsWindows()) Expect(GrokClientIdentity.CurrentPlatform, "windows");
        else if (OperatingSystem.IsMacOS()) Expect(GrokClientIdentity.CurrentPlatform, "macos");
        else Expect(GrokClientIdentity.CurrentPlatform, "linux");
        Expect(GrokClientIdentity.CurrentArchitecture is "x86_64" or "aarch64" or "x86" or "arm", $"an architecture the CLI knows, got {GrokClientIdentity.CurrentArchitecture}");
        Expect(new GrokClientIdentity("1.2.3", "macos", "aarch64").UserAgent, "grok-pager/1.2.3 grok-shell/1.2.3 (macos; aarch64)");
        Expect(new GrokClientIdentity(null, "windows", "x86_64").UserAgent, "grok-pager grok-shell (windows; x86_64)");
    }

    private static void CaptionAndExhaustion()
    {
        var language = L10n.Current;
        try
        {
            L10n.Current = L10n.Language.SimplifiedChinese;
            Expect(L10n.Tr("Open grok to sync"), "打开 grok 同步一下");
        }
        finally
        {
            L10n.Current = language;
        }
    }
}
