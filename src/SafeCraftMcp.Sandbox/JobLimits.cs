using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SafeCraftMcp.Sandbox;

/// <summary>The resource limits a session's process tree gets.</summary>
public sealed record ResourceLimits(long MaxMemoryBytes, int MaxProcesses);

/// <summary>
/// Puts a sandboxed app into a Windows Job Object: a cap on the memory its whole
/// process tree commits, a cap on how many processes it runs at once, and kill-on-close. MXC
/// v1 has no limit fields of its own. The job handle lives as long as the session: when it's
/// closed, by the gateway closing the session or by Windows when the gateway process ends (a crash
/// included), every process in the job is killed. Jobs nest, so this works whether or not MXC
/// put the process in a job of its own.
/// </summary>
public static partial class JobLimits
{
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint LimitActiveProcess = 0x0008;
    private const uint LimitJobMemory = 0x0200;
    private const uint LimitKillOnJobClose = 0x2000;

    /// <summary>Puts process <paramref name="processId"/> into a new job with the limits; the caller owns the returned handle.</summary>
    public static SafeFileHandle Apply(uint processId, ResourceLimits limits)
    {
        using var process = OpenProcess(ProcessSetQuota | ProcessTerminate | ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
            throw Error($"Couldn't open the sandboxed process {processId} to limit it");
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw Error("Couldn't create a job object");
        try
        {
            var info = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation
                {
                    LimitFlags = LimitActiveProcess | LimitJobMemory | LimitKillOnJobClose,
                    ActiveProcessLimit = (uint)limits.MaxProcesses,
                },
                JobMemoryLimit = (UIntPtr)(ulong)limits.MaxMemoryBytes,
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw Error("Couldn't set the job's limits");
            if (!AssignProcessToJobObject(job, process))
                throw Error($"Couldn't put the sandboxed process {processId} into its job");
            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>The limits a job really has, read back from Windows (for tests and diagnostics).</summary>
    public static (ResourceLimits Limits, bool KillOnClose) Read(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, JobObjectExtendedLimitInformation, out var info, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), IntPtr.Zero))
            throw Error("Couldn't read the job's limits");
        var flags = info.BasicLimitInformation.LimitFlags;
        return (new ResourceLimits(
                (flags & LimitJobMemory) != 0 ? (long)(ulong)info.JobMemoryLimit : 0,
                (flags & LimitActiveProcess) != 0 ? (int)info.BasicLimitInformation.ActiveProcessLimit : 0),
            (flags & LimitKillOnJobClose) != 0);
    }

    /// <summary>How many processes in the job are still running (for tests and diagnostics).</summary>
    public static int ActiveProcesses(SafeFileHandle job)
    {
        if (!QueryAccountingInformation(job, JobObjectBasicAccountingInformation, out var info, (uint)Marshal.SizeOf<BasicAccountingInformation>(), IntPtr.Zero))
            throw Error("Couldn't read the job's process count");
        return (int)info.ActiveProcesses;
    }

    /// <summary>Whether the process is in any job (MXC's, ours, or both).</summary>
    public static bool IsInJob(uint processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        return !process.IsInvalid && IsProcessInJob(process, IntPtr.Zero, out var inJob) && inJob;
    }

    private static Win32Exception Error(string what)
    {
        var code = Marshal.GetLastPInvokeError();
        return new Win32Exception(code, $"{what}: {new Win32Exception(code).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out ExtendedLimitInformation info, uint length, IntPtr returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryAccountingInformation(SafeFileHandle job, int infoClass, out BasicAccountingInformation info, uint length, IntPtr returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(SafeFileHandle process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
