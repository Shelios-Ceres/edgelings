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
        using var child = Child.Start(job, start, permitLaunch);
        if (child is null) return null;
        child.Input.Dispose(); // EOF on the child's stdin.
        using var reader = new StreamReader(child.Output, start.StandardOutputEncoding ?? Encoding.UTF8);
        var stdout = reader.ReadToEndAsync();
        var stderr = child.Error.CopyToAsync(Stream.Null);
        if (!WaitForExit(child.Process, timeout, ct))
        {
            TerminateProcess(child.Process, 1); // Close the writers before disposing pending pipe reads.
            return null;
        }
        if (!GetExitCodeProcess(child.Process, out var exitCode) || exitCode != 0) return null;
        if (!Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2))) return null;
        return stdout.Result.Trim();
    }

    /// 一个起在 Job Object 里的子进程和它的三根管道。Start 在 CreateProcess 之后、
    /// ResumeThread 之前登记 OwnProcesses；Dispose 结束进程、注销登记、关句柄。
    private sealed class Child : IDisposable
    {
        public readonly AnonymousPipeServerStream Input = new(PipeDirection.Out, HandleInheritability.Inheritable);
        public readonly AnonymousPipeServerStream Output = new(PipeDirection.In, HandleInheritability.Inheritable);
        public readonly AnonymousPipeServerStream Error = new(PipeDirection.In, HandleInheritability.Inheritable);
        public nint Process;
        private ProcessInfo _info;
        private IDisposable? _own;
        private nint _list, _values;
        private bool _initialized;

        public static Child? Start(nint job, ProcessStartInfo start, Func<Func<bool>, bool>? permitLaunch)
        {
            var child = new Child();
            if (child.Launch(job, start, permitLaunch)) return child;
            child.Dispose();
            return null;
        }

        private bool Launch(nint job, ProcessStartInfo start, Func<Func<bool>, bool>? permitLaunch)
        {
            var si = new StartupInfoEx();
            si.Startup.Size = Marshal.SizeOf<StartupInfoEx>();
            si.Startup.Flags = 0x100; // STARTF_USESTDHANDLES
            si.Startup.Input = Input.ClientSafePipeHandle.DangerousGetHandle();
            si.Startup.Output = Output.ClientSafePipeHandle.DangerousGetHandle();
            si.Startup.Error = Error.ClientSafePipeHandle.DangerousGetHandle();

            nuint size = 0;
            InitializeProcThreadAttributeList(0, 2, 0, ref size);
            if (size == 0) return false;
            _list = Marshal.AllocHGlobal(checked((nint)size));
            _values = Marshal.AllocHGlobal(4 * IntPtr.Size);
            _initialized = InitializeProcThreadAttributeList(_list, 2, 0, ref size);
            if (!_initialized) return false;
            si.Attributes = _list;
            Marshal.Copy(new[] { si.Startup.Input, si.Startup.Output, si.Startup.Error, job }, 0, _values, 4);
            if (!UpdateProcThreadAttribute(_list, 0, 0x20002, _values, (nuint)(3 * IntPtr.Size), 0, 0)
                || !UpdateProcThreadAttribute(_list, 0, 0x2000D, _values + 3 * IntPtr.Size, (nuint)IntPtr.Size, 0, 0))
                return false; // HANDLE_LIST and JOB_LIST respectively

            var environment = string.Concat(start.Environment.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                .Select(k => k.Key + "=" + k.Value + "\0")) + "\0";
            var command = new StringBuilder($"\"{start.FileName}\" {start.Arguments}");
            const uint flags = 0x00000004 | 0x08000000 | 0x00000400 | 0x00080000;
            // SUSPENDED | NO_WINDOW | UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT
            bool Launch()
            {
                if (!CreateProcessW(start.FileName, command, 0, 0, true, flags, environment,
                        string.IsNullOrEmpty(start.WorkingDirectory) ? null : start.WorkingDirectory, ref si, out _info))
                    return false;
                Process = _info.Process;
                // 挂起状态下就登记：它和它的子孙都不算用户的 agent 开着。
                _own = OwnProcesses.Register((int)_info.ProcessId);
                return ResumeThread(_info.Thread) == 1;
            }
            if (!(permitLaunch is null ? Launch() : permitLaunch(Launch))) return false;
            Input.DisposeLocalCopyOfClientHandle();
            Output.DisposeLocalCopyOfClientHandle();
            Error.DisposeLocalCopyOfClientHandle();
            return true;
        }

        public void Dispose()
        {
            if (_info.Process != 0)
            {
                if (WaitForSingleObject(_info.Process, 0) == 258) TerminateProcess(_info.Process, 1);
                CloseHandle(_info.Thread);
                CloseHandle(_info.Process);
                _info = default;
            }
            _own?.Dispose();
            _own = null;
            if (_initialized) DeleteProcThreadAttributeList(_list);
            _initialized = false;
            if (_values != 0) Marshal.FreeHGlobal(_values);
            if (_list != 0) Marshal.FreeHGlobal(_list);
            _values = _list = 0;
            Input.Dispose();
            Output.Dispose();
            Error.Dispose();
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
        var job = CreateJob(singleProcess: true);
        if (job == 0) return null;
        try
        {
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

    /// 带 KILL_ON_JOB_CLOSE 的 Job Object；singleProcess 时再加 ActiveProcessLimit = 1。失败返回 0。
    private static nint CreateJob(bool singleProcess)
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return 0;
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | (singleProcess ? JobObjectLimitActiveProcess : 0),
                ActiveProcessLimit = singleProcess ? 1u : 0u,
            },
        };
        if (SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            return job;
        }
        CloseHandle(job);
        return 0;
    }

    internal enum LineResult { Line, Eof, Timeout }

    /// 起在 Job Object 里的子进程，一问一答地用：往 stdin 写一行，逐行读 stdout。
    /// Dispose 先关 Job（KILL_ON_JOB_CLOSE 杀掉整棵进程树），再注销 OwnProcesses
    /// 登记、关管道。stderr 一直排空，免得子进程写满管道卡住。
    [SupportedOSPlatform("windows")]
    internal sealed class Conversation : IDisposable
    {
        private readonly nint _job;
        private readonly Child _child;
        private readonly StreamReader _reader;
        private Task<string?>? _pending;
        private bool _inputClosed;
        private bool _disposed;

        private Conversation(nint job, Child child, Encoding encoding)
        {
            _job = job;
            _child = child;
            _reader = new StreamReader(child.Output, encoding);
            _ = child.Error.CopyToAsync(Stream.Null).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
        }

        /// 起不来返回 null。singleProcess：ActiveProcessLimit = 1，子进程自己再起进程会被内核拒绝。
        internal static Conversation? Start(ProcessStartInfo start, bool singleProcess,
            Func<Func<bool>, bool>? permitLaunch = null)
        {
            var job = CreateJob(singleProcess);
            if (job == 0) return null;
            Child? child = null;
            Conversation? result = null;
            try
            {
                child = Child.Start(job, start, permitLaunch);
                if (child is not null) result = new Conversation(job, child, start.StandardOutputEncoding ?? new UTF8Encoding(false));
            }
            catch
            {
            }
            if (result is null)
            {
                CloseHandle(job); // 先关 Job 杀进程，再释放管道。
                child?.Dispose();
            }
            return result;
        }

        /// 写一行（UTF-8、不带 BOM）；管道已断开返回 false。
        internal bool WriteLine(string line)
        {
            if (_inputClosed) return false;
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                _child.Input.Write(bytes, 0, bytes.Length);
                _child.Input.Flush();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        /// 关掉 stdin：子进程读到 EOF。
        internal void CloseInput()
        {
            if (_inputClosed) return;
            _inputClosed = true;
            try { _child.Input.Dispose(); } catch (IOException) { }
        }

        /// 读一行 stdout，最多等 timeout。超时不丢数据：这一行还在读，下一次接着等。
        internal LineResult ReadLine(TimeSpan timeout, out string? line, CancellationToken ct = default)
        {
            line = null;
            _pending ??= ReadLineObserved();
            try
            {
                if (!_pending.Wait(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout, ct)) return LineResult.Timeout;
            }
            catch (OperationCanceledException)
            {
                return LineResult.Timeout;
            }
            catch (AggregateException)
            {
                _pending = null;
                return LineResult.Eof;
            }
            line = _pending.Result;
            _pending = null;
            return line is null ? LineResult.Eof : LineResult.Line;
        }

        private async Task<string?> ReadLineObserved()
        {
            try { return await _reader.ReadLineAsync(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { return null; }
        }

        /// 进程自己退出了就返回 true；超时、取消返回 false。
        internal bool WaitForExit(TimeSpan timeout, CancellationToken ct = default) =>
            WindowsJobProcess.WaitForExit(_child.Process, timeout, ct);

        internal int? ExitCode => WaitForSingleObject(_child.Process, 0) == 0 && GetExitCodeProcess(_child.Process, out var code)
            ? (int)code : null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CloseHandle(_job); // 整棵进程树随 Job 一起死。
            _child.Dispose();
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
