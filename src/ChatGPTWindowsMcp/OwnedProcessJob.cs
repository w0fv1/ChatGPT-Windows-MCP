using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChatGPTWindowsMcp;

/// <summary>
/// Owns launcher child processes through a Windows Job Object.
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE ensures that processes assigned to the
/// job are terminated when the launcher exits, including abnormal exits where
/// normal async cleanup cannot run.
/// </summary>
internal sealed class OwnedProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeJobHandle _handle;
    private readonly LogSink _log;
    private bool _disposed;

    private OwnedProcessJob(SafeJobHandle handle, LogSink log)
    {
        _handle = handle;
        _log = log;
    }

    public static OwnedProcessJob? TryCreate(LogSink log)
    {
        try
        {
            var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            var length = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            if (!NativeMethods.SetInformationJobObject(
                    handle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    ref info,
                    (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "SetInformationJobObject failed.");
            }

            log.Write("已启用 Windows Job Object 子进程回收保护。");
            return new OwnedProcessJob(handle, log);
        }
        catch (Exception ex)
        {
            log.Write($"警告：无法启用 Windows Job Object，退出时将使用常规进程树清理：{ex.Message}");
            return null;
        }
    }

    public void TryAssign(Process process, string source)
    {
        if (_disposed || process.HasExited)
            return;

        try
        {
            if (!NativeMethods.AssignProcessToJobObject(_handle, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                _log.Write($"警告：无法将 {source} PID {process.Id} 加入 Job Object（Win32 {error}）。");
            }
        }
        catch (Exception ex)
        {
            _log.Write($"警告：将 {source} 加入 Job Object 时失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _handle.Dispose();
    }

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeJobHandle() : base(ownsHandle: true) { }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeJobHandle CreateJobObject(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeJobHandle job,
            JobObjectInfoType infoType,
            ref JobObjectExtendedLimitInformation info,
            uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }}
