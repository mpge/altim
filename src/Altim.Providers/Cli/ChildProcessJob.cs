using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Altim.Providers.Cli;

/// <summary>
/// A Windows job object that every provider CLI Altim starts is put into, so the tree dies
/// with Altim.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CliRunner"/> already kills a process tree on a timeout, and that covers the
/// command that hangs. It does not cover the one that is working normally when Altim quits:
/// a provider CLI started a moment before Quit goes on running, and the ones Altim runs are
/// Node processes that take a second or two to notice their standard input has gone. So
/// closing Altim left a `claude` or `codex` process behind for a couple of seconds with
/// nothing on screen to explain it, and a user watching Task Manager sees an application that
/// did not really close.
/// </para>
/// <para>
/// A job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes that the kernel's
/// problem. The handle is held for the life of the process and is never closed on purpose;
/// when Altim exits — tidily, or killed, or crashed — the handle closes with it and every
/// process still in the job is terminated. It is the one teardown that does not depend on
/// Altim getting a chance to run any code.
/// </para>
/// <para>
/// Windows only. Everything here is behind <see cref="OperatingSystem.IsWindows"/> and the
/// whole type is inert elsewhere, which is the same shape the rest of Altim's platform
/// differences take. Every failure is silent by design: not being able to create a job is
/// exactly the situation that existed before this type, and it is not worth failing a
/// provider read over.
/// </para>
/// </remarks>
internal static class ChildProcessJob
{
    /// <summary>Terminate every process in the job when the last handle to it closes.</summary>
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>The information class for <see cref="JOBOBJECT_EXTENDED_LIMIT_INFORMATION"/>.</summary>
    private const int JobObjectExtendedLimitInformation = 9;

    private static readonly Lock Gate = new();
    private static IntPtr _job;
    private static bool _tried;

    /// <summary>
    /// Puts a freshly started process into the job, so that it cannot outlive Altim.
    /// </summary>
    /// <param name="process">The process that was just started.</param>
    /// <remarks>
    /// Called immediately after <see cref="Process.Start()"/> and before anything is read
    /// from the process, so the window in which a child is not yet in the job is a few
    /// microseconds wide. A child that starts children of its own inherits the job from its
    /// parent, so the whole tree is covered by this one call.
    /// </remarks>
    public static void Adopt(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IntPtr job = Ensure();
        if (job == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _ = AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // The process exited between starting and being adopted, or its handle is not
            // available. Either way there is nothing left to keep alive.
        }
    }

    /// <summary>
    /// The job, created on first use. Created once whether or not it worked, so a machine
    /// that refuses job objects is asked once rather than on every command.
    /// </summary>
    private static IntPtr Ensure()
    {
        lock (Gate)
        {
            if (_tried)
            {
                return _job;
            }

            _tried = true;

            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformation,
                    ref limits,
                    (uint)Unsafe.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                // A job without the limit would hold the children without killing them,
                // which is worse than not having one: it is the same outcome plus a handle.
                _ = CloseHandle(job);
                return IntPtr.Zero;
            }

            // Deliberately never closed. Closing it is what kills the tree, and the moment
            // for that is the process exiting — which the kernel does for us.
            _job = job;
            return job;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int informationClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

#pragma warning disable IDE1006 // The Win32 names, so the layouts can be checked against the documentation.
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
#pragma warning restore IDE1006
}
