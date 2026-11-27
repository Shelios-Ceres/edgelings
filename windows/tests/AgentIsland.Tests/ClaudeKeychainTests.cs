using System.Text;
using System.Text.Json.Nodes;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Claude's login in the macOS Keychain: discovery and the freshest login (read only;
/// Limen no longer writes the keychain). The logic runs
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
            ("keychain: a security that fails is neither a sign-out nor an empty keychain", SecurityFailureIsNotAbsence),
            ("keychain: the modification stamp changes when the item changes", StampChanges),
            ("keychain: a throwaway keychain file is read, and a stamp comes back (macOS)", ThrowawayKeychain),
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
        public bool HexSecrets;
        /// `security` itself failing (a timeout, a locked keychain): exit code 1, never "not found".
        public bool FailReads;

        /// Exit 44 is what `security` says for an item that is not there.
        public string? RunWithCode(IReadOnlyList<string> a, out int exitCode)
        {
            if (FailReads)
            {
                exitCode = 1;
                return null;
            }
            var output = Run(a);
            exitCode = output is not null ? 0 : a[0] == "find-generic-password" ? ClaudeKeychain.ItemNotFound : 1;
            return output;
        }

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
                case "dump-keychain":
                    return string.Concat(Items.Select(i => $"keychain: \"/fake\"\nattributes:\n    \"svce\"<blob>=\"{i.Key}\"\n"));
                default:
                    return null;
            }
        }
    }

    private static ClaudeKeychain Over(Fake fake) => new(fake.RunWithCode, Base, null);

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
        Expect(!item.ToString().Contains("R1"), "an item prints without its secret");
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

    private static void SecurityFailureIsNotAbsence()
    {
        var fake = new Fake();
        fake.Items[Base] = ("me", Login("A", "R1"), 1);
        var keychain = Over(fake);
        keychain.Read();
        Expect(!keychain.LastReadFailed, "a good read did not fail");
        fake.FailReads = true;
        Expect(keychain.Read() is null && keychain.LastReadFailed, "a read that could not ask says so");
        fake.FailReads = false;
        fake.Items.Clear();
        Expect(keychain.Read() is null && !keychain.LastReadFailed, "an empty keychain answered with 44 is plain absence");
        var blind = new ClaudeKeychain(_ => null, Base, null);
        Expect(blind.Read() is null && blind.LastReadFailed, "a runner that gives no exit code never proves absence");
    }

    private static void StampChanges()
    {
        var fake = new Fake();
        var keychain = Over(fake);
        Expect(keychain.ModificationStamp() is null, "no item, no stamp");
        fake.Items[Base] = ("me", Login("A", "R1"), 1);
        var first = keychain.ModificationStamp();
        Expect(first is not null, "a login landing gives a stamp");
        fake.Items[Base] = ("me", Login("A2", "R2"), 2);
        Expect(keychain.ModificationStamp() != first, "a change moves the stamp");
    }

    /// On a Mac only: the real /usr/bin/security against a keychain file made
    /// for this test, under a service name of its own. The login keychain is
    /// not in the search list, so it is never read.
    private static void ThrowawayKeychain()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "limen-keychain-" + Guid.NewGuid().ToString("N"))).FullName;
        var file = Path.Combine(folder, "throwaway.keychain-db");
        string? Security(params string[] arguments) => ClaudeKeychain.RunSecurity(arguments);
        try
        {
            Expect(Security("create-keychain", "-p", "throwaway", file) is not null, "create-keychain");
            Expect(Security("unlock-keychain", "-p", "throwaway", file) is not null, "unlock-keychain");
            Expect(Security("set-keychain-settings", file) is not null, "set-keychain-settings");
            var service = "Limen-test-" + Guid.NewGuid().ToString("N");
            Expect(Security("add-generic-password", "-s", service, "-a", "tester", "-w", Login("A", "R1"), file) is not null, "seed the item");
            var keychain = new ClaudeKeychain(ClaudeKeychain.RunSecurity, service, file);
            var item = keychain.Read() ?? throw new Exception("the seeded item was not read");
            Expect(item.Account, "tester");
            Expect(item.AccessToken, "A");
            var stamp = keychain.ModificationStamp();
            Expect(stamp is not null, "stamp");
        }
        finally
        {
            Security("delete-keychain", file);
            try { Directory.Delete(folder, recursive: true); } catch { }
        }
    }
}
