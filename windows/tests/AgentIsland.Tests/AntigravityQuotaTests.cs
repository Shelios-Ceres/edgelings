using System.Text;
using System.Text.Json;
using AgentIsland.Core;
using AgentIsland.Localization;
using AgentIsland.Model;
using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Antigravity's local language server: which process owns a port, where the
/// CSRF token comes from, how discovery picks a port. Its quota is gone; the
/// session watch (AntigravityLiveCascades) and the cost reader still use it.
public static class AntigravityQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("only this process's own hub file gives its token", TestHubToken),
            ("the hub file's token comes before the command line", TestCsrfTokenPrefersHub),
            ("a server without GetUnleashData is found and authenticated from its hub file", TestDiscoverWithoutUnleashData),
            ("the single-process Windows Job Object blocks grandchild spawns", TestSingleProcessJob),
            ("only Antigravity's own language server is asked, never Windsurf's or Codeium's", TestLanguageServerOwner),
            ("a port is traced to the process listening on it", TestPortOwner),
            ("a port with several different listeners has no owner, and an exact 127.0.0.1 bind wins", TestPortOwnerAmbiguity),
            ("a process's CSRF token is read once, and looked for again only after a while", TestCsrfTokenIsRemembered),
            ("a quoted or equals-separated CSRF token is still read from the command line", TestParseCsrfToken),
            ("discovery skips a 401-only port when another Antigravity port authenticates", TestDiscoverPrefersAuthenticatedPort),
            ("a helper that hangs is stopped at its timeout", TestHelperTimeout),
        };
        // This machine's own hub files must never reach a test.
        var realHub = AntigravityLanguageServer.HubFiles;
        AntigravityLanguageServer.HubFiles = Array.Empty<string>;
        try
        {
            foreach (var (name, test) in tests)
            {
                test();
                Console.WriteLine($"PASS antigravity server: {name}");
            }
        }
        finally
        {
            AntigravityLanguageServer.HubFiles = realHub;
        }
        Console.WriteLine("AntigravityQuotaTests GREEN");
    }

    private sealed class HubFolder : IDisposable
    {
        private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("limen-hub-");
        public List<string> Files { get; } = new();

        public string Add(object content, DateTime writtenUtc)
        {
            var path = Path.Combine(_folder.FullName, $"hub_{Files.Count}.json");
            File.WriteAllText(path, content as string ?? JsonSerializer.Serialize(content));
            File.SetLastWriteTimeUtc(path, writtenUtc);
            Files.Add(path);
            return path;
        }

        public void Dispose() => _folder.Delete(recursive: true);
    }

    private static void TestHubToken()
    {
        var prevHub = AntigravityLanguageServer.HubFiles;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        try
        {
            using var hub = new HubFolder();
            AntigravityLanguageServer.HubFiles = () => hub.Files;
            AntigravityLanguageServer.ListListeningPorts = pid => pid == 41852 ? new List<int> { 55845, 55846 } : new List<int> { 60000 };
            var started = new DateTime(2026, 11, 25, 0, 28, 6, DateTimeKind.Utc);

            // A reused pid's file from days ago, another process's file, and garbage come first.
            hub.Add(new { pid = 41852, httpsPort = 55845, httpPort = 55846, csrfToken = "old" }, started.AddDays(-3));
            hub.Add(new { pid = 7, httpsPort = 60000, httpPort = 60001, csrfToken = "other" }, started.AddSeconds(1));
            hub.Add("not json", started.AddSeconds(1));
            Expect(AntigravityLanguageServer.HubToken(41852, started) is null, "no file of this process yet");

            hub.Add(new { pid = 41852, httpsPort = 1, httpPort = 2, csrfToken = "wrong-ports" }, started.AddSeconds(1));
            Expect(AntigravityLanguageServer.HubToken(41852, started) is null, "a file naming ports the process does not hold is not its own");
            hub.Add(new { pid = 99, httpsPort = 55845, httpPort = 55846, csrfToken = "wrong-pid" }, started.AddSeconds(1));
            Expect(AntigravityLanguageServer.HubToken(41852, started) is null, "a fresh file naming its ports but another pid is not its own");

            // Written a fraction of a second after the process started, ms dropped on one side.
            hub.Add(new { pid = 41852, httpsPort = 55845, httpPort = 55846, lsVersion = "2.35.1", csrfToken = "tok-hub" }, started.AddMilliseconds(-500));
            Expect(AntigravityLanguageServer.HubToken(41852, started), "tok-hub");
            Expect(AntigravityLanguageServer.HubToken(41852, null) is null, "an unknown start time proves nothing");

            // Rewritten by the same server later: the newest file wins.
            hub.Add(new { pid = 41852, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-newer" }, started.AddMinutes(5));
            Expect(AntigravityLanguageServer.HubToken(41852, started), "tok-newer");
        }
        finally
        {
            AntigravityLanguageServer.HubFiles = prevHub;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
        }
    }

    private static void TestCsrfTokenPrefersHub()
    {
        var prevHub = AntigravityLanguageServer.HubFiles;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        var prevStarted = AntigravityLanguageServer.StartTime;
        var reads = 0;
        try
        {
            using var hub = new HubFolder();
            var pid = Environment.ProcessId;
            var started = DateTime.UtcNow.AddMinutes(-1);
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.HubFiles = () => hub.Files;
            AntigravityLanguageServer.StartTime = _ => started;
            AntigravityLanguageServer.ListListeningPorts = _ => new List<int> { 55845 };
            AntigravityLanguageServer.ReadCommandLine = _ => { reads++; return null; };

            // The server has not written its file yet: the command line is read once.
            Expect(AntigravityLanguageServer.CsrfToken(pid) is null, "nothing yet");
            Expect(reads, 1);
            // The file appears: it is found on the next ask, without waiting out the command line's pause.
            hub.Add(new { pid, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-hub" }, DateTime.UtcNow);
            Expect(AntigravityLanguageServer.CsrfToken(pid), "tok-hub");
            Expect(AntigravityLanguageServer.CsrfToken(pid), "tok-hub");
            Expect(reads, 1);
        }
        finally
        {
            AntigravityLanguageServer.HubFiles = prevHub;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    // The desktop's 2.35 server answers GetUnleashData with 404.
    private static void TestDiscoverWithoutUnleashData()
    {
        var prevCall = AntigravityLanguageServer.CallRpc;
        var prevPids = AntigravityLanguageServer.ListAntigravityProcessIds;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevOwner = AntigravityLanguageServer.ResolvePortOwner;
        var prevStarted = AntigravityLanguageServer.StartTime;
        var prevHub = AntigravityLanguageServer.HubFiles;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        try
        {
            using var hub = new HubFolder();
            var started = DateTime.UtcNow.AddMinutes(-1);
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.StartTime = _ => started;
            AntigravityLanguageServer.HubFiles = () => hub.Files;
            AntigravityLanguageServer.ReadCommandLine = _ => "language_server.exe --read_host_bridge_token_from_stdin";
            AntigravityLanguageServer.ListAntigravityProcessIds = () => new List<int> { 201 };
            AntigravityLanguageServer.ListListeningPorts = _ => new List<int> { 55845, 55846 };
            AntigravityLanguageServer.ResolvePortOwner = port => port is 55845 or 55846 ? 201 : null;
            hub.Add(new { pid = 201, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-hub" }, DateTime.UtcNow);
            var asked = new List<string>();
            AntigravityLanguageServer.CallRpc = (method, port, _, token, _) =>
            {
                asked.Add($"{method}@{port}:{token ?? "-"}");
                return Task.FromResult<AntigravityLanguageServer.Reply?>(port != 55845
                    ? null
                    : method == "GetUnleashData" ? new(404, Array.Empty<byte>())
                    : token == "tok-hub" ? new(200, Array.Empty<byte>())
                    : new(401, Array.Empty<byte>()));
            };
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            Expect(asked.Contains("GetUserStatus@55845:tok-hub"), $"the probe authenticates with the hub token, asked {string.Join(", ", asked)}");

            // A server that has neither method is no RPC port.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.CallRpc = (_, _, _, _, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(new(404, Array.Empty<byte>()));
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult() is null, "404 on both probes is not a port");

            // An older server without GetUserStatus is still found through GetUnleashData,
            // and so is one whose GetUserStatus is slow (null) or failing (500).
            foreach (var first in new AntigravityLanguageServer.Reply?[] { new(404, Array.Empty<byte>()), null, new(500, Array.Empty<byte>()) })
            {
                AntigravityLanguageServer.ForgetCachedPort();
                AntigravityLanguageServer.CallRpc = (method, port, _, _, _) => Task.FromResult(
                    port != 55845 ? null
                    : method == "GetUnleashData" ? new AntigravityLanguageServer.Reply(200, Array.Empty<byte>())
                    : first);
                Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            }

            // A token the server refuses is dropped: the hub file rewritten with a new one is read.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            var accepted = "tok-rotated";
            AntigravityLanguageServer.CallRpc = (_, port, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port == 55845 && token == accepted ? new(200, Array.Empty<byte>()) : new(401, Array.Empty<byte>()));
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            hub.Add(new { pid = 201, httpsPort = 55845, httpPort = 55846, csrfToken = "tok-rotated" }, DateTime.UtcNow.AddSeconds(5));
            AntigravityLanguageServer.ForgetCachedPort();
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)55845);
            Expect(AntigravityLanguageServer.CsrfToken(201), "tok-rotated");
        }
        finally
        {
            AntigravityLanguageServer.CallRpc = prevCall;
            AntigravityLanguageServer.ListAntigravityProcessIds = prevPids;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ResolvePortOwner = prevOwner;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.HubFiles = prevHub;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static IDisposable GeminiHomeWithAntigravity()
    {
        var root = Path.Combine(AntigravityCredentials.GeminiHome, "antigravity");
        var created = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        return new Cleanup(() => { if (created) Directory.Delete(root, recursive: true); });
    }

    private sealed class Cleanup(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    // Neither the app nor agy is running: Limen starts neither, so nothing is
    // read, and the card keeps its last numbers with their time. It is not a
    // sign-in problem and must not look like one.
    // Nothing was ever read: the card says how to get a reading, in words
    // that are not about signing in, and the numbers show a dash.
    // A week of an app left closed must not blank the card: the snapshot
    // is kept at any age, with the time it was read.
    // The server is there but turns Limen away. That is a real fault worth
    // saying, so the caption stays and the numbers stay under it.
    // Hiding Antigravity cancels the poll's token: a call still waiting on
    // the server ends at once instead of running beside the next poll.
    private static void TestSingleProcessJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        var single = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c echo ok",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Expect(WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5)), "ok");
        var folder = Directory.CreateTempSubdirectory("limen-job-");
        try
        {
            single.WorkingDirectory = folder.FullName;
            single.Arguments = "/d /c echo started>marker";
            Expect(WindowsJobProcess.Run(0, single, TimeSpan.FromSeconds(5), null) is null,
                "invalid job prevents creation");
            Expect(WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5), _ => false) is null,
                "hidden launch is denied");
            Expect(!File.Exists(Path.Combine(folder.FullName, "marker")), "main thread never ran");
            File.WriteAllText(Path.Combine(folder.FullName, "child.cmd"), "@echo child>child");
            single.Arguments = "/d /c echo started>parent & cmd.exe /d /c child.cmd";
            WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5));
            Expect(File.Exists(Path.Combine(folder.FullName, "parent")) && !File.Exists(Path.Combine(folder.FullName, "child")),
                "parent entered its job and the child produced no marker");
            single.Environment["ZZ_LIMEN_SENTINEL"] = "isolated-value";
            single.Arguments = "/d /c echo %ZZ_LIMEN_SENTINEL%";
            Expect(WindowsJobProcess.RunInSingleProcessJob(single, TimeSpan.FromSeconds(5)), "isolated-value");
        }
        finally { folder.Delete(true); }

        var wait = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 12 127.0.0.1");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Expect(WindowsJobProcess.RunInSingleProcessJob(wait, TimeSpan.FromMilliseconds(150)) is null, "timeout stops the job");
        Expect(clock.Elapsed < TimeSpan.FromSeconds(5), "pipe cleanup respects timeout");

        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(400)))
        {
            var sleeper = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            clock.Restart();
            Expect(WindowsJobProcess.RunInSingleProcessJob(sleeper, TimeSpan.FromSeconds(60), null, cancel.Token) is null, "cancel stops the job");
            Expect(clock.Elapsed < TimeSpan.FromSeconds(5), "cancel ends the wait long before the timeout");
        }

        var spawnChild = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c cmd.exe /c echo SHOULD_NOT_RUN",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Expect(WindowsJobProcess.RunInSingleProcessJob(spawnChild, TimeSpan.FromSeconds(5)) is null,
            "ActiveProcessLimit=1 must block the child's own CreateProcess calls at kernel level");
    }

    private static void TestLanguageServerOwner()
    {
        const string server = "language_server_windows_x64";
        static bool Is(string name, string? path, string? parent) => AntigravityLanguageServer.IsAntigravityProcess(name, path, parent);
        Expect(Is("agy", null, "pwsh") && Is("Antigravity", null, "explorer") && Is("antigravity-cli", null, null), "the CLI and the desktop app");
        Expect(Is(server, @"C:\Users\u\AppData\Local\Programs\Antigravity\resources\app\extensions\antigravity\bin\language_server_windows_x64.exe", "Antigravity"),
            "the desktop app's own server");
        Expect(Is(server, @"D:\Tools\AG\bin\language_server_windows_x64.exe", "Antigravity"), "one Antigravity started, wherever it is installed");
        Expect(Is(server, @"C:\Users\u\AppData\Local\Programs\Antigravity\resources\bin\language_server.exe", null), "one under an Antigravity folder");
        Expect(!Is(server, @"C:\Users\u\AppData\Local\Programs\Windsurf\resources\app\extensions\windsurf\bin\language_server_windows_x64.exe", "Windsurf"),
            "Windsurf's server");
        Expect(!Is(server, @"C:\Users\u\.vscode\extensions\codeium.codeium-1.40.0\dist\abc\language_server_windows_x64.exe", "Code"),
            "Codeium's server in VS Code");
        Expect(!Is(server, null, null), "a server whose path and parent can't be read");
        Expect(!Is("language_server_antigravity", @"C:\Tools\codeium\language_server_antigravity.exe", "Code"), "the file name alone says nothing");
        Expect(!Is("Windsurf", null, null), "another editor");
        var self = AntigravityLanguageServer.Processes().FirstOrDefault(p => p.Id == Environment.ProcessId);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        Expect(self.Name == current.ProcessName && self.ParentId > 0, $"the process list names this very process, got '{self.Name}'");
    }

    private static void TestPortOwner()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            Expect(AntigravityLanguageServer.PortOwner(port), (int?)Environment.ProcessId);
            Expect(AntigravityLanguageServer.ListeningPorts(Environment.ProcessId).Contains(port), "and listed under it");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void TestPortOwnerAmbiguity()
    {
        var rows = AntigravityLanguageServer.ParseLsofRows("p101\nn*:5000\nn127.0.0.1:5001\np202\nn*:5000\nn127.0.0.1:5002\np303\nn*:5002\nn[::1]:5003\n");
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5000), null);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5001), (int?)101);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5002), (int?)202);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 5003), null);
        Expect(AntigravityLanguageServer.OwnerOf(rows, 6000), null);
        Expect(AntigravityLanguageServer.OwnerOf(new[] { (7, 5000, false), (7, 5000, false) }, 5000), (int?)7);
        Expect(AntigravityLanguageServer.ParseLsof("p101\nn127.0.0.1:5001\n").Count, 1);
    }

    private static void TestCsrfTokenIsRemembered()
    {
        var previous = AntigravityLanguageServer.ReadCommandLine;
        var reads = 0;
        try
        {
            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.ReadCommandLine = _ =>
            {
                reads++;
                return "language_server.exe --csrf_token abc-123 --extension_server_port 5000";
            };
            var pid = Environment.ProcessId;
            Expect(AntigravityLanguageServer.CsrfToken(pid), "abc-123");
            Expect(AntigravityLanguageServer.CsrfToken(pid), "abc-123");
            Expect(reads, 1);

            AntigravityLanguageServer.ForgetCsrfTokens();
            AntigravityLanguageServer.ReadCommandLine = _ =>
            {
                reads++;
                return null;
            };
            reads = 0;
            Expect(AntigravityLanguageServer.CsrfToken(pid) is null, "a read that fails finds no token");
            Expect(AntigravityLanguageServer.CsrfToken(pid) is null, "and is not repeated on the next fetch");
            Expect(reads, 1);
        }
        finally
        {
            AntigravityLanguageServer.ReadCommandLine = previous;
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static void TestParseCsrfToken()
    {
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token abc-123 --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token=abc-123 --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token \"abc-123\" --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token=\"abc-123\" --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe \"--csrf_token=abc-123\" --port 1"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token 'abc-123'"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token='abc-123'"), "abc-123");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --csrf_token \"\" --port 1") is null, "empty quotes carry no token");
        Expect(AntigravityLanguageServer.ParseCsrfToken("language_server.exe --port 1") is null, "missing flag returns null");
    }

    private static void TestDiscoverPrefersAuthenticatedPort()
    {
        var prevCall = AntigravityLanguageServer.CallRpc;
        var prevPids = AntigravityLanguageServer.ListAntigravityProcessIds;
        var prevPorts = AntigravityLanguageServer.ListListeningPorts;
        var prevOwner = AntigravityLanguageServer.ResolvePortOwner;
        var prevStarted = AntigravityLanguageServer.StartTime;
        AntigravityLanguageServer.StartTime = _ => Now.UtcDateTime;
        var prevRead = AntigravityLanguageServer.ReadCommandLine;
        try
        {
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();

            // Port 51001 rejects everything with 401 (no token); port 51002 authenticates with its own token.
            AntigravityLanguageServer.ListAntigravityProcessIds = () => new List<int> { 101, 102 };
            AntigravityLanguageServer.ListListeningPorts = pid => pid == 101 ? new List<int> { 51001 } : new List<int> { 51002 };
            AntigravityLanguageServer.ResolvePortOwner = port => port == 51001 ? 101 : port == 51002 ? 102 : null;
            AntigravityLanguageServer.ReadCommandLine = pid => pid == 102 ? "language_server.exe --csrf_token \"good-tok\"" : "Antigravity.exe";
            AntigravityLanguageServer.CallRpc = (_, port, _, token, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port == 51002 && token == "good-tok"
                    ? new AntigravityLanguageServer.Reply(200, Array.Empty<byte>())
                    : new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));

            var discovered = AntigravityLanguageServer.Discover().GetAwaiter().GetResult();
            Expect(discovered, (int?)51002);

            // When only a 401 port exists, Discover returns it as a fallback without pinning it,
            // so a later ready port is picked up immediately.
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
            var cliStarted = false;
            AntigravityLanguageServer.ListAntigravityProcessIds = () => cliStarted ? new List<int> { 101, 103 } : new List<int> { 101 };
            AntigravityLanguageServer.ListListeningPorts = pid => pid == 101 ? new List<int> { 51001 } : new List<int> { 51003 };
            AntigravityLanguageServer.ResolvePortOwner = port => port == 51001 ? 101 : port == 51003 ? 103 : null;
            AntigravityLanguageServer.CallRpc = (_, port, _, _, _) => Task.FromResult<AntigravityLanguageServer.Reply?>(
                port == 51003
                    ? new AntigravityLanguageServer.Reply(200, Array.Empty<byte>())
                    : new AntigravityLanguageServer.Reply(401, Array.Empty<byte>()));

            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)51001);
            cliStarted = true;
            Expect(AntigravityLanguageServer.Discover().GetAwaiter().GetResult(), (int?)51003);
        }
        finally
        {
            AntigravityLanguageServer.CallRpc = prevCall;
            AntigravityLanguageServer.ListAntigravityProcessIds = prevPids;
            AntigravityLanguageServer.ListListeningPorts = prevPorts;
            AntigravityLanguageServer.ResolvePortOwner = prevOwner;
            AntigravityLanguageServer.StartTime = prevStarted;
            AntigravityLanguageServer.ReadCommandLine = prevRead;
            AntigravityLanguageServer.ForgetCachedPort();
            AntigravityLanguageServer.ForgetCsrfTokens();
        }
    }

    private static void TestHelperTimeout()
    {
        var quick = AntigravityLanguageServer.Output(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo hello" : "-c \"echo hello\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, TimeSpan.FromSeconds(10));
        Expect(quick, "hello");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hung = AntigravityLanguageServer.Output(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c ping -n 30 127.0.0.1" : "-c \"sleep 30\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, TimeSpan.FromMilliseconds(300));
        Expect(hung is null, "a helper still running at its timeout gives nothing");
        Expect(clock.Elapsed < TimeSpan.FromSeconds(5), $"and is not waited out, took {clock.Elapsed.TotalSeconds:F1}s");
    }

}
