using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AgentIsland.Core;

namespace AgentIsland.Usage;

[SupportedOSPlatform("windows")]
internal static class WindowsJobProcess
{
    // Assign the job as part of CreateProcess itself: even a crash of the parent
    // before ResumeThread leaves the child owned by the kill-on-close job.
    internal static string? Run(nint job, ProcessStartInfo start, TimeSpan timeout, Func<Func<bool>, bool>? permitLaunch,
        CancellationToken ct = default)
    {
        using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var output = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var error = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var si = new StartupInfoEx();
        si.Startup.Size = Marshal.SizeOf<StartupInfoEx>();
        si.Startup.Flags = 0x100; // STARTF_USESTDHANDLES
        si.Startup.Input = input.ClientSafePipeHandle.DangerousGetHandle();
        si.Startup.Output = output.ClientSafePipeHandle.DangerousGetHandle();
        si.Startup.Error = error.ClientSafePipeHandle.DangerousGetHandle();

        nuint size = 0;
        InitializeProcThreadAttributeList(0, 2, 0, ref size);
        if (size == 0) return null;
        var list = Marshal.AllocHGlobal(checked((nint)size));
        var values = Marshal.AllocHGlobal(4 * IntPtr.Size);
        bool initialized = false;
        ProcessInfo child = default;
        IDisposable? own = null;
        try
        {
            initialized = InitializeProcThreadAttributeList(list, 2, 0, ref size);
            if (!initialized) return null;
            si.Attributes = list;
            Marshal.Copy(new[] { si.Startup.Input, si.Startup.Output, si.Startup.Error, job }, 0, values, 4);
            if (!UpdateProcThreadAttribute(list, 0, 0x20002, values, (nuint)(3 * IntPtr.Size), 0, 0)
                || !UpdateProcThreadAttribute(list, 0, 0x2000D, values + 3 * IntPtr.Size, (nuint)IntPtr.Size, 0, 0))
                return null; // HANDLE_LIST and JOB_LIST respectively

            var environment = string.Concat(start.Environment.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(k => k.Key + "=" + k.Value + "\0")) + "\0";
            var command = new StringBuilder($"\"{start.FileName}\" {start.Arguments}");
            const uint flags = 0x00000004 | 0x08000000 | 0x00000400 | 0x00080000;
            // SUSPENDED | NO_WINDOW | UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT
            bool Launch()
            {
                if (!CreateProcessW(start.FileName, command, 0, 0, true, flags, environment,
                        string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref si, out child))
                    return false;
                // 挂起状态下就登记：它和它的子孙都不算用户的 agent 开着。
                own = OwnProcesses.Register((int)child.ProcessId);
                return ResumeThread(child.Thread) == 1;
            }
            if (!(permitLaunch is null ? Launch() : permitLaunch(Launch))) return null;
            input.Dispose(); // EOF on the child's stdin.
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            using var reader = new StreamReader(output, start.StandardOutputEncoding ?? Encoding.UTF8);
            var stdout = reader.ReadToEndAsync();
            var stderr = error.CopyToAsync(Stream.Null);
            if (!WaitForExit(child.Process, timeout, ct))
            {
                TerminateProcess(child.Process, 1); // Close the writers before disposing pending pipe reads.
                return null;
            }
            if (!GetExitCodeProcess(child.Process, out var exitCode) || exitCode != 0) return null;
            if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2))) return null;
            return stdout.Result.Trim();
        }
        finally
        {
            if (child.Process != 0)
            {
                if (WaitForSingleObject(child.Process, 0) == 258) TerminateProcess(child.Process, 1);
                CloseHandle(child.Thread);
                CloseHandle(child.Process);
            }
            own?.Dispose();
            if (initialized) DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(values);
            Marshal.FreeHGlobal(list);
        }
    }

    /// True when the process exited; false on timeout or cancellation. The wait
    /// is sliced so a cancel is noticed within one slice.
    internal static bool WaitForExit(nint process, TimeSpan timeout, CancellationToken ct)
    {
        var slice = TimeSpan.FromMilliseconds(200);
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (ct.IsCancellationRequested) return false;
            var left = timeout - clock.Elapsed;
            if (left <= TimeSpan.Zero) return false;
            const uint waitObject0 = 0, waitTimeout = 258;
            var result = WaitForSingleObject(process, checked((uint)(left < slice ? left : slice).TotalMilliseconds));
            if (result == waitObject0) return true;
            // WAIT_FAILED returns at once: looping on it would spin until the timeout.
            if (result != waitTimeout) return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedSize;
        public nint ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, bool inheritHandles, uint flags, string environment, string? directory,
        ref StartupInfoEx startup, out ProcessInfo process);

    [DllImport("kernel32.dll")] private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll")] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint key, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll")] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(nint process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);

    /// Runs a child inside a Job Object with ActiveProcessLimit = 1 and
    /// KILL_ON_JOB_CLOSE: the kernel refuses the child's own CreateProcess
    /// calls, and closing the job kills it. Processes another process starts
    /// on its behalf (COM activation, ShellExecute) are not stopped. A
    /// cancelled token ends the child within a fraction of a second.
    [SupportedOSPlatform("windows")]
    internal static string? RunInSingleProcessJob(ProcessStartInfo start, TimeSpan timeout,
        Func<Func<bool>, bool>? permitLaunch = null, CancellationToken ct = default)
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return null;
        try
        {
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitActiveProcess | JobObjectLimitKillOnJobClose,
                    ActiveProcessLimit = 1,
                },
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                return null;
            }
            return Run(job, start, timeout, permitLaunch, ct);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(job);
        }
    }

    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitActiveProcess = 0x00000008;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);
}
