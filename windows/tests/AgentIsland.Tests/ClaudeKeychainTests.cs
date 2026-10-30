using System.Text;
using System.Text.Json.Nodes;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Claude's login in the macOS Keychain: discovery, the freshest login, the
/// write-back of a rotated pair into the item it came from. The logic runs
/// against an in-memory stand-in for /usr/bin/security everywhere; on a Mac
/// the same calls also run against a throwaway keychain file. The real login
/// keychain is never touched, and no token is ever printed.
public static class ClaudeKeychainTests
{
    private const string Base = "Claude Code-credentials";

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("keychain: the plain item is read with its account, plan and pair", ReadsPlainItem),
            ("keychain: with no plain item, the freshest suffixed login wins and an MCP cache is ignored", FreshestSuffixed),
            ("keychain: the metadata listing's account and date are read from the last quoted value", MetadataParsing),
            ("keychain: a secret printed as hex is decoded", HexSecret),
            ("keychain: a rotated pair goes back to the same item and everything else in it is kept", WritesBackToSameItem),
            ("keychain: a pair the CLI rotated meanwhile is left alone", SupersededIsLeftAlone),
            ("keychain: a write that fails reports failure", WriteFailure),
            ("keychain: the modification stamp changes when the item is written", StampChanges),
            ("keychain: an expired token is refreshed and the rotated pair lands in the keychain item", EndToEndRefresh),
            ("keychain: a throwaway keychain file round-trips a read and a write-back (macOS)", ThrowawayKeychain),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine("PASS " + name);
        }
        Console.WriteLine("ClaudeKeychainTests GREEN");
    }

    private static string Login(string access, string refresh, double expires = 100) => new JsonObject
    {
        ["claudeAiOauth"] = new JsonObject
        {
            ["accessToken"] = access,
            ["refreshToken"] = refresh,
            ["expiresAt"] = expires,
            ["subscriptionType"] = "max",
            ["scopes"] = new JsonArray("user:inference", "user:profile"),
        },
        ["mcpOAuth"] = new JsonObject { ["server"] = "kept" },
    }.ToJsonString();

    /// What security prints for an item's metadata and for a dump.
    private sealed class Fake
    {
        public readonly Dictionary<string, (string Account, string Json, int Version)> Items = new(StringComparer.Ordinal);
        public readonly List<IReadOnlyList<string>> Writes = new();
        public bool FailWrites;
        public bool HexSecrets;

        public string? Run(IReadOnlyList<string> a)
        {
            string? Arg(string flag)
            {
                for (var i = 0; i < a.Count - 1; i++) if (a[i] == flag) return a[i + 1];
                return null;
            }
            switch (a[0])
            {
                case "find-generic-password":
                {
                    if (Arg("-s") is not { } service || !Items.TryGetValue(service, out var item)) return null;
                    if (Arg("-a") is { } account && account != item.Account) return null;
                    if (a.Contains("-w"))
                    {
                        return (HexSecrets ? Convert.ToHexString(Encoding.UTF8.GetBytes(item.Json)) : item.Json) + "\n";
                    }
                    return "keychain: \"/fake/login.keychain-db\"\nclass: \"genp\"\nattributes:\n"
                        + $"    \"acct\"<blob>=\"{item.Account}\"\n    \"mdat\"<timedate>=0x00  \"2026103012{item.Version:D4}Z\\000\"\n    \"svce\"<blob>=\"{service}\"\n";
                }
                case "add-generic-password":
                {
                    Writes.Add(a.ToList());
                    if (FailWrites) return null;
                    var service = Arg("-s")!;
                    Items[service] = (Arg("-a")!, Arg("-w")!, Items.TryGetValue(service, out var old) ? old.Version + 1 : 1);
                    return "";
                }
                case "dump-keychain":
                    return string.Concat(Items.Select(i => $"keychain: \"/fake\"\nattributes:\n    \"svce\"<blob>=\"{i.Key}\"\n"));
                default:
                    return null;
            }
        }
    }

    private static ClaudeKeychain Over(Fake fake) => new(fake.Run, Base, null);

    private static void ReadsPlainItem()
    {
        var fake = new Fake();
        fake.Items[Base] = ("me", Login("A", "R1"), 1);
        var item = Over(fake).Read() ?? throw new Exception("not read");
        Expect(item.Service, Base);
        Expect(item.Account, "me");
        Expect(item.AccessToken, "A");
        Expect(item.RefreshToken, "R1");
        Expect(item.SubscriptionType, "max");
    }

    private static void FreshestSuffixed()
    {
        var fake = new Fake();
        fake.Items[Base + "-aaaa1111"] = ("me", Login("OLD", "R-old", 50), 1);
        fake.Items[Base + "-bbbb2222"] = ("me", Login("NEW", "R-new", 500), 1);
        fake.Items[Base + "-cccc3333"] = ("me", "{\"mcpOAuth\":{\"x\":1}}", 1);
        fake.Items["Some other service"] = ("me", Login("OTHER", "R-other", 9999), 1);
        var item = Over(fake).Read() ?? throw new Exception("not read");
        Expect(item.Service, Base + "-bbbb2222");
        Expect(item.AccessToken, "NEW");
        Expect(Over(new Fake()).Read() is null, "an empty keychain reads nothing");
    }

    private static void MetadataParsing()
    {
        var listing = "attributes:\n    \"acct\"<blob>=0x6D65C3A9  \"meé\"\n    \"mdat\"<timedate>=0x3230  \"20261030120000Z\\000\"\n    \"icmt\"<blob>=<NULL>\n";
        Expect(ClaudeKeychain.MetadataValue(listing, "acct"), "meé");
        Expect(ClaudeKeychain.MetadataValue(listing, "mdat"), "20261030120000Z\\000");
        Expect(ClaudeKeychain.MetadataValue(listing, "icmt") is null, "<NULL> is no value");
        Expect(ClaudeKeychain.MetadataValue(listing, "svce") is null, "a missing line is no value");
        var dump = "    \"svce\"<blob>=\"Claude Code-credentials-b\"\n    \"svce\"<blob>=\"Claude Code-credentials-a\"\n    \"svce\"<blob>=\"Claude Code-credentials\"\n    \"svce\"<blob>=\"Other-b\"\n    \"svce\"<blob>=\"Claude Code-credentials-a\"\n";
        Expect(string.Join(",", ClaudeKeychain.ParseServiceNames(dump, Base)), "Claude Code-credentials-a,Claude Code-credentials-b");
    }

    private static void HexSecret()
    {
        var fake = new Fake { HexSecrets = true };
        fake.Items[Base] = ("me", Login("Ä-token", "R1"), 1);
        Expect(Over(fake).Read()?.AccessToken, "Ä-token");
    }

    private static void WritesBackToSameItem()
    {
        var fake = new Fake();
        fake.Items[Base] = ("plain", Login("P", "RP", 900), 1);
        fake.Items[Base + "-dead0000"] = ("suffixed", Login("S", "RS", 100), 1);
        var keychain = Over(fake);
        var item = keychain.Read()!;
        Expect(item.Service, Base);
        Expect(keychain.WriteBack(item.Service, "A2", "R2", 12345, "RP"), ClaudeKeychain.WriteResult.Written);
        var written = JsonNode.Parse(fake.Items[Base].Json)!;
        Expect((string?)written["claudeAiOauth"]!["accessToken"], "A2");
        Expect((string?)written["claudeAiOauth"]!["refreshToken"], "R2");
        Expect((long?)written["claudeAiOauth"]!["expiresAt"], 12345L);
        Expect((string?)written["claudeAiOauth"]!["subscriptionType"], "max");
        Expect((string?)written["mcpOAuth"]!["server"], "kept");
        Expect(fake.Items[Base].Account, "plain");
        var other = JsonNode.Parse(fake.Items[Base + "-dead0000"].Json)!;
        Expect((string?)other["claudeAiOauth"]!["refreshToken"], "RS");
        Expect(fake.Writes.Count, 1);
        Expect(fake.Writes[0].Contains("-U") && fake.Writes[0].Contains("-a") && fake.Writes[0].Contains("plain"), "an update of the same account");
    }

    private static void SupersededIsLeftAlone()
    {
        var fake = new Fake();
        fake.Items[Base] = ("me", Login("A", "R1"), 1);
        var keychain = Over(fake);
        var item = keychain.Read()!;
        fake.Items[Base] = ("me", Login("B", "R-by-cli"), 2);
        Expect(keychain.WriteBack(item.Service, "A2", "R2", 1, item.RefreshToken!), ClaudeKeychain.WriteResult.Superseded);
        Expect(fake.Writes.Count, 0);
        Expect(keychain.WriteBack("Gone", "A2", "R2", 1, "R1"), ClaudeKeychain.WriteResult.Failed);
    }

    private static void WriteFailure()
    {
        var fake = new Fake { FailWrites = true };
        fake.Items[Base] = ("me", Login("A", "R1"), 1);
        var keychain = Over(fake);
        Expect(keychain.WriteBack(Base, "A2", "R2", 1, "R1"), ClaudeKeychain.WriteResult.Failed);
    }

    private static void StampChanges()
    {
        var fake = new Fake();
        var keychain = Over(fake);
        Expect(keychain.ModificationStamp() is null, "no item, no stamp");
        fake.Items[Base] = ("me", Login("A", "R1"), 1);
        var first = keychain.ModificationStamp();
        Expect(first is not null, "a login landing gives a stamp");
        keychain.WriteBack(Base, "A2", "R2", 1, "R1");
        Expect(keychain.ModificationStamp() != first, "a write moves the stamp");
    }

    private static void EndToEndRefresh()
    {
        var fake = new Fake();
        fake.Items[Base] = ("me", Login("A", "R1"), 1);
        var previousKeychain = ClaudeCredentials.KeychainOverride;
        var previousRefresh = ClaudeCredentials.RefreshToken;
        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", null);
            ClaudeCredentials.KeychainOverride = Over(fake);
            var refreshedWith = new List<string>();
            ClaudeCredentials.RefreshToken = refresh =>
            {
                refreshedWith.Add(refresh);
                return Task.FromResult<ClaudeCredentials.RefreshResult>(
                    new ClaudeCredentials.RefreshResult.Refreshed(new ClaudeCredentials.RefreshedTokens("B", "R2", 777)));
            };
            var seen = new List<string>();
            var result = ClaudeCredentials.ResolveUsage((token, _) =>
            {
                seen.Add(token);
                return Task.FromResult<ClaudeCredentials.ProbeOutcome>(token == "B"
                    ? new ClaudeCredentials.ProbeOutcome.Success(AppUsage.Empty)
                    : new ClaudeCredentials.ProbeOutcome.Unauthorized());
            }).GetAwaiter().GetResult();
            Expect(result is ClaudeCredentials.Resolution.Usage, "the refreshed token's usage is returned");
            Expect(string.Join(",", refreshedWith), "R1");
            Expect(string.Join(",", seen), "A,B");
            var saved = JsonNode.Parse(fake.Items[Base].Json)!;
            Expect((string?)saved["claudeAiOauth"]!["refreshToken"], "R2");
            Expect((string?)saved["claudeAiOauth"]!["accessToken"], "B");
            Expect((string?)saved["mcpOAuth"]!["server"], "kept");
        }
        finally
        {
            ClaudeCredentials.KeychainOverride = previousKeychain;
            ClaudeCredentials.RefreshToken = previousRefresh;
            Environment.SetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN", envToken);
        }
    }

    /// On a Mac only: the real /usr/bin/security against a keychain file made
    /// for this test, under a service name of its own. The login keychain is
    /// not in the search list, so it is never read.
    private static void ThrowawayKeychain()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "edgelings-keychain-" + Guid.NewGuid().ToString("N"))).FullName;
        var file = Path.Combine(folder, "throwaway.keychain-db");
        string? Security(params string[] arguments) => ClaudeKeychain.RunSecurity(arguments);
        try
        {
            Expect(Security("create-keychain", "-p", "throwaway", file) is not null, "create-keychain");
            Expect(Security("unlock-keychain", "-p", "throwaway", file) is not null, "unlock-keychain");
            Expect(Security("set-keychain-settings", file) is not null, "set-keychain-settings");
            var service = "Edgelings-test-" + Guid.NewGuid().ToString("N");
            Expect(Security("add-generic-password", "-s", service, "-a", "tester", "-w", Login("A", "R1"), file) is not null, "seed the item");
            var keychain = new ClaudeKeychain(ClaudeKeychain.RunSecurity, service, file);
            var item = keychain.Read() ?? throw new Exception("the seeded item was not read");
            Expect(item.Account, "tester");
            Expect(item.AccessToken, "A");
            var stamp = keychain.ModificationStamp();
            Expect(stamp is not null, "stamp");
            Expect(keychain.WriteBack(service, "B", "R2", 4242, "R-wrong"), ClaudeKeychain.WriteResult.Superseded);
            Expect(keychain.WriteBack(service, "B", "R2", 4242, "R1"), ClaudeKeychain.WriteResult.Written);
            var after = keychain.Read() ?? throw new Exception("not read after the write");
            Expect(after.AccessToken, "B");
            Expect(after.RefreshToken, "R2");
            Expect((string?)after.Root["mcpOAuth"]!["server"], "kept");
            Expect(after.Account, "tester");
        }
        finally
        {
            Security("delete-keychain", file);
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
