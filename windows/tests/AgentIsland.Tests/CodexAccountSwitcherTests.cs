using System.Text;
using System.Text.Json;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

public static class CodexAccountSwitcherTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("refreshed credentials retain account identity", RefreshedIdentity),
            ("same email does not merge workspaces or users", DistinctIdentities),
            ("unknown credential formats only match exact bytes", UnknownIdentity),
            ("rotation skips every alias of the refreshed current account", RotationSkipsCurrent),
            ("activating current account never restores old credentials", SameAccountIsNoOp),
            ("switching preserves refreshed credentials in every saved alias", RefreshedCredentialsAreSaved),
            ("an unsaved outgoing login is backed up before switching", UnsavedCredentialsAreSaved),
            ("backup failure aborts without replacing live credentials", BackupFailureAborts),
            ("unreadable live credentials cannot be replaced", UnreadableLiveAborts),
            ("a missing live login can activate a saved account", MissingLiveCanActivate),
            ("account activation rejects paths outside its store", OutsideStoreIsRejected),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine("CodexAccountSwitcherTests GREEN");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static byte[] Credential(string account, string generation, string user = "user-one") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            email = "same@example.invalid",
            tokens = new
            {
                account_id = account,
                id_token = Jwt(new Dictionary<string, object>
                {
                    ["sub"] = user,
                    ["https://api.openai.com/auth"] = new { chatgpt_account_id = account, chatgpt_user_id = user },
                    ["exp"] = generation,
                }),
                access_token = "FAKE-ACCESS-" + generation,
                refresh_token = "FAKE-REFRESH-" + generation,
            },
            last_refresh = generation,
        });

    private static string Jwt(object claims) => "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".fake";

    private static void RefreshedIdentity()
    {
        Expect(CodexAccountSwitcher.SameAccount(Credential("workspace-one", "old"), Credential("workspace-one", "new")),
            "token rotation changed stable identity");
        var noAccountField = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tokens = new { id_token = Jwt(new Dictionary<string, object>
            {
                ["sub"] = "user-one",
                ["https://api.openai.com/auth"] = new { chatgpt_account_id = "workspace-one", chatgpt_user_id = "user-one" },
            }) },
        });
        Expect(CodexAccountSwitcher.SameAccount(noAccountField, Credential("workspace-one", "new")),
            "JWT account identity was not recognized");
    }

    private static void DistinctIdentities()
    {
        Expect(!CodexAccountSwitcher.SameAccount(Credential("personal", "old"), Credential("team", "new")),
            "same email incorrectly merged different workspaces");
        Expect(!CodexAccountSwitcher.SameAccount(Credential("team", "old", "user-a"), Credential("team", "new", "user-b")),
            "different users in the same workspace were merged");
    }

    private static void UnknownIdentity()
    {
        var exact = Encoding.UTF8.GetBytes("unknown-format");
        Expect(CodexAccountSwitcher.SameAccount(exact, exact), "exact legacy snapshot no longer matched");
        foreach (var raw in new[] { "null", "[]", "{broken", "{\"tokens\":null}", "{\"email\":\"same@example.invalid\"}" })
            Expect(!CodexAccountSwitcher.SameAccount(Encoding.UTF8.GetBytes(raw), Credential("workspace", "fresh")),
                "malformed or email-only credentials guessed an identity");
        var invalidJwt = Encoding.UTF8.GetBytes("{\"tokens\":{\"account_id\":\"workspace\",\"id_token\":\"x.!!.x\"}}");
        var plain = Encoding.UTF8.GetBytes("{\"tokens\":{\"account_id\":\"workspace\",\"access_token\":\"fake\"}}");
        Expect(CodexAccountSwitcher.SameAccount(invalidJwt, plain), "malformed optional JWT defeated explicit account identity");
    }

    private static void RotationSkipsCurrent()
    {
        using var fixture = new Fixture();
        fixture.Save("a-current", Credential("current", "old"));
        fixture.Save("b-alias", Credential("current", "older"));
        fixture.Save("c-other", Credential("other", "old"));
        fixture.Live(Credential("current", "new"));
        Expect(CodexAccountSwitcher.ActiveLabel(fixture.Home) == "a-current", "refreshed current account was unrecognized");
        Expect(CodexAccountSwitcher.RotationCandidate(new HashSet<string>(), fixture.Home)?.Label == "c-other",
            "rotation selected the current account or one of its aliases");
        Expect(CodexAccountSwitcher.RotationCandidate(new HashSet<string> { "c-other" }, fixture.Home) is null,
            "rotation ignored tried accounts");
        fixture.Live(Credential("other", "fresh"));
        Expect(CodexAccountSwitcher.RotationCandidate(new HashSet<string> { "a-current" }, fixture.Home) is null,
            "rotation retried an exhausted account through another alias");
    }

    private static void SameAccountIsNoOp()
    {
        using var fixture = new Fixture();
        var saved = fixture.Save("current", Credential("current", "old"));
        var fresh = Credential("current", "fresh");
        fixture.Live(fresh);
        Expect(CodexAccountSwitcher.Activate(saved, fixture.Home), "same-account selection failed");
        Expect(fixture.LiveBytes().SequenceEqual(fresh), "same-account activation rolled back refreshed tokens");
    }

    private static void RefreshedCredentialsAreSaved()
    {
        using var fixture = new Fixture();
        var current = fixture.Save("current", Credential("current", "old"));
        var alias = fixture.Save("current-alias", Credential("current", "older"));
        var other = fixture.Save("other", Credential("other", "saved"));
        var fresh = Credential("current", "fresh");
        fixture.Live(fresh);
        Expect(CodexAccountSwitcher.Activate(other, fixture.Home), "switch failed");
        Expect(File.ReadAllBytes(current.Path).SequenceEqual(fresh), "saved account lost fresh credentials");
        Expect(File.ReadAllBytes(alias.Path).SequenceEqual(fresh), "alias still holds stale credentials");
        Expect(CodexAccountSwitcher.Activate(current, fixture.Home), "switch back failed");
        Expect(fixture.LiveBytes().SequenceEqual(fresh), "switch back restored old credentials");
    }

    private static void UnsavedCredentialsAreSaved()
    {
        using var fixture = new Fixture();
        var outgoing = Credential("unsaved", "fresh");
        fixture.Live(outgoing);
        var incoming = fixture.Save("incoming", Credential("incoming", "old"));
        Expect(CodexAccountSwitcher.Activate(incoming, fixture.Home), "new outgoing account could not be backed up");
        Expect(CodexAccountSwitcher.Accounts(fixture.Home).Count(account =>
            File.ReadAllBytes(account.Path).SequenceEqual(outgoing)) == 1, "outgoing snapshot not preserved exactly once");
    }

    private static void BackupFailureAborts()
    {
        using var fixture = new Fixture();
        var current = fixture.Save("current", Credential("current", "old"));
        var incoming = fixture.Save("incoming", Credential("incoming", "saved"));
        var fresh = Credential("current", "fresh");
        fixture.Live(fresh);
        using var blockReplacement = new FileStream(current.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Expect(!CodexAccountSwitcher.Activate(incoming, fixture.Home), "switch ignored the failed outgoing backup");
        Expect(fixture.LiveBytes().SequenceEqual(fresh), "backup failure replaced live credentials");
    }

    private static void UnreadableLiveAborts()
    {
        using var fixture = new Fixture();
        var incoming = fixture.Save("incoming", Credential("incoming", "saved"));
        var outgoing = Credential("current", "fresh");
        fixture.Live(outgoing);
        using (var locked = new FileStream(fixture.LivePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Expect(!CodexAccountSwitcher.Activate(incoming, fixture.Home), "unreadable live login was treated as absent");
            Expect(CodexAccountSwitcher.RotationCandidate(new HashSet<string>(), fixture.Home) is null,
                "rotation continued while live identity was unreadable");
        }
        Expect(fixture.LiveBytes().SequenceEqual(outgoing), "unreadable live login was overwritten");
    }

    private static void MissingLiveCanActivate()
    {
        using var fixture = new Fixture();
        var data = Credential("incoming", "saved");
        var incoming = fixture.Save("incoming", data);
        Expect(CodexAccountSwitcher.Activate(incoming, fixture.Home), "missing live login unnecessarily blocked activation");
        Expect(fixture.LiveBytes().SequenceEqual(data), "saved login was not installed");
    }

    private static void OutsideStoreIsRejected()
    {
        using var fixture = new Fixture();
        fixture.Live(Credential("current", "fresh"));
        var outside = Path.Combine(fixture.Home, "outside.json");
        File.WriteAllBytes(outside, Credential("other", "saved"));
        Expect(!CodexAccountSwitcher.Activate(new("outside", outside, null), fixture.Home),
            "activation accepted a credential path outside the store");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AgentIslandAccountTests"));
        public string Home { get; }
        public string LivePath => Path.Combine(Home, "auth.json");
        public Fixture()
        {
            Home = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, "agentisland-accounts"));
        }
        public void Live(byte[] data) => File.WriteAllBytes(LivePath, data);
        public byte[] LiveBytes() => File.ReadAllBytes(LivePath);
        public CodexAccountSwitcher.Account Save(string label, byte[] data)
        {
            var path = Path.Combine(Home, "agentisland-accounts", label + ".json");
            File.WriteAllBytes(path, data);
            return new(label, path, "same@example.invalid");
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Home);
            if (!resolved.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its temporary root");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
