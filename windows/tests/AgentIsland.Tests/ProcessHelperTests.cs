using AgentIsland.Usage;
using static AgentIsland.Tests.Check;

namespace AgentIsland.Tests;

/// Process helpers: the single-process job, which process owns a loopback
/// port, and the bounded helper run.
public static class ProcessHelperTests
{
    public static void RunAll()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("the single-process Windows Job Object blocks grandchild spawns", TestSingleProcessJob),
            ("a port is traced to the process listening on it", TestPortOwner),
            ("a port with several different listeners has no owner, and an exact 127.0.0.1 bind wins", TestPortOwnerAmbiguity),
            ("a helper that hangs is stopped at its timeout", TestHelperTimeout),
        };
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"PASS process helper: {name}");
        }
        Console.WriteLine("ProcessHelperTests GREEN");
    }

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

    private static void TestPortOwner()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            Expect(LoopbackPorts.PortOwner(port), (int?)Environment.ProcessId);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void TestPortOwnerAmbiguity()
    {
        var rows = LoopbackPorts.ParseLsofRows("p101\nn*:5000\nn127.0.0.1:5001\np202\nn*:5000\nn127.0.0.1:5002\np303\nn*:5002\nn[::1]:5003\n");
        Expect(LoopbackPorts.OwnerOf(rows, 5000), null);
        Expect(LoopbackPorts.OwnerOf(rows, 5001), (int?)101);
        Expect(LoopbackPorts.OwnerOf(rows, 5002), (int?)202);
        Expect(LoopbackPorts.OwnerOf(rows, 5003), null);
        Expect(LoopbackPorts.OwnerOf(rows, 6000), null);
        Expect(LoopbackPorts.OwnerOf(new[] { (7, 5000, false), (7, 5000, false) }, 5000), (int?)7);
        Expect(LoopbackPorts.ParseLsof("p101\nn127.0.0.1:5001\n").Count, 1);
    }

    private static void TestHelperTimeout()
    {
        var quick = LoopbackPorts.Output(new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? "/c echo hello" : "-c \"echo hello\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }, TimeSpan.FromSeconds(10));
        Expect(quick, "hello");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var hung = LoopbackPorts.Output(new System.Diagnostics.ProcessStartInfo
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
