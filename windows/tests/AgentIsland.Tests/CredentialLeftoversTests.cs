using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// 旧版本留下的残留副本清理：只删匹配的旧文件，不动凭证文件和别的东西。
public static class CredentialLeftoversTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef";

    public static void RunAll()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentisland-leftovers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            OnlyMatchingOldCopiesAreDeleted(directory);
            Console.WriteLine("PASS leftovers: only old tmp/bak copies are deleted, credential files and others stay");
            MissingDirectoryIsFine(directory);
            Console.WriteLine("PASS leftovers: a missing directory is not an error");
            Console.WriteLine("CredentialLeftoversTests GREEN");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Put(string directory, string name, DateTime written)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "synthetic");
        File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    private static void OnlyMatchingOldCopiesAreDeleted(string directory)
    {
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        var old = now.AddHours(-3);
        var file = Path.Combine(directory, ".credentials.json");

        var credentials = Put(directory, ".credentials.json", old);
        var codexAuth = Put(directory, "auth.json", old);
        var oldTmp = Put(directory, ".credentials.json.tmp-" + Hex, old);
        var oldDotTmp = Put(directory, "..credentials.json.tmp-" + Hex, old);
        var oldBak = Put(directory, ".credentials.json.bak-" + Hex, old);
        var freshTmp = Put(directory, ".credentials.json.tmp-" + Hex.Replace('0', '1'), now.AddMinutes(-1));
        var wrongLength = Put(directory, ".credentials.json.tmp-abc", old);
        var notHex = Put(directory, ".credentials.json.bak-" + new string('z', 32), old);
        var otherFile = Put(directory, "auth.json.tmp-" + Hex, old);
        var unrelated = Put(directory, "notes.txt", old);

        CredentialLeftovers.CleanUp(file, new DateTimeOffset(now));

        Expect(File.Exists(credentials), ".credentials.json is never touched");
        Expect(File.Exists(codexAuth), "auth.json is never touched");
        Expect(!File.Exists(oldTmp), "an old .tmp- copy is deleted");
        Expect(!File.Exists(oldDotTmp), "an old dot-prefixed .tmp- copy is deleted");
        Expect(!File.Exists(oldBak), "an old .bak- copy is deleted");
        Expect(File.Exists(freshTmp), "a copy written a minute ago may still be in use: kept");
        Expect(File.Exists(wrongLength), "a name that is not the exact pattern is kept");
        Expect(File.Exists(notHex), "a non-hex suffix is kept");
        Expect(File.Exists(otherFile), "another credential file's copy is not this file's business");
        Expect(File.Exists(unrelated), "unrelated files are kept");
        Expect(File.ReadAllText(credentials), "synthetic");

        // 同一个目录里 auth.json 的残留按 auth.json 的名字清理，并且同样不碰 auth.json 本身。
        CredentialLeftovers.CleanUp(Path.Combine(directory, "auth.json"), new DateTimeOffset(now));
        Expect(!File.Exists(otherFile), "auth.json's old copy is deleted when asked for auth.json");
        Expect(File.Exists(codexAuth), "auth.json itself stays");
        Expect(File.Exists(credentials), ".credentials.json still stays");
    }

    private static void MissingDirectoryIsFine(string directory)
    {
        CredentialLeftovers.CleanUp(Path.Combine(directory, "no-such-folder", ".credentials.json"));
    }
}
