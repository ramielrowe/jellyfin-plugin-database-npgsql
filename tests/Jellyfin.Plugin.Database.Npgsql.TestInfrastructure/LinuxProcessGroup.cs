using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

internal sealed class LinuxProcessIdentity : IDisposable
{
    private int _pidFileDescriptor;

    private LinuxProcessIdentity(int processId, int parentProcessId, int processGroupId, ulong startTime, int pidFileDescriptor)
    {
        ProcessId = processId;
        ParentProcessId = parentProcessId;
        ProcessGroupId = processGroupId;
        StartTime = startTime;
        _pidFileDescriptor = pidFileDescriptor;
    }

    public int ProcessId { get; }

    public int ParentProcessId { get; }

    public int ProcessGroupId { get; }

    public ulong StartTime { get; }

    public static LinuxProcessIdentity Capture(int processId)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Safe test-process control requires Linux pidfds and /proc.");
        }

        var descriptor = pidfd_open(processId, 0);
        if (descriptor < 0)
        {
            throw new IOException("Could not open a pidfd for the launched process.");
        }

        try
        {
            if (HasExited(descriptor))
            {
                throw new IOException("The launched process exited before its pidfd identity was captured.");
            }

            var (parentProcessId, processGroupId, startTime) = ReadIdentity(processId);
            if (HasExited(descriptor))
            {
                throw new IOException("The launched process exited while its pidfd identity was captured.");
            }

            var result = new LinuxProcessIdentity(processId, parentProcessId, processGroupId, startTime, descriptor);
            descriptor = -1;
            return result;
        }
        finally
        {
            if (descriptor >= 0)
            {
                _ = close(descriptor);
            }
        }
    }

    public static LinuxProcessIdentity ReportedExited(int processId, int parentProcessId, int processGroupId, ulong startTime)
        => new(processId, parentProcessId, processGroupId, startTime, -1);

    public static IReadOnlyList<LinuxProcessIdentity> CaptureDescendants(int rootProcessId)
    {
        var all = new List<LinuxProcessIdentity>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var processId) || processId == rootProcessId)
            {
                continue;
            }

            try
            {
                all.Add(Capture(processId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A process exited while its pidfd was opened and identified.
            }
        }

        var descendants = new List<LinuxProcessIdentity>();
        var parents = new HashSet<int> { rootProcessId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var candidate in all)
            {
                if (parents.Contains(candidate.ParentProcessId) && parents.Add(candidate.ProcessId))
                {
                    descendants.Add(candidate);
                    changed = true;
                }
            }
        }

        foreach (var unrelated in all.Except(descendants))
        {
            unrelated.Dispose();
        }

        return descendants;
    }

    public static LinuxProcessIdentity? AwaitDedicatedGroup(Process process, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                return null;
            }

            LinuxProcessIdentity? identity = null;
            try
            {
                identity = Capture(process.Id);
                if (identity.ProcessGroupId == identity.ProcessId)
                {
                    var result = identity;
                    identity = null;
                    return result;
                }
            }
            catch (IOException)
            {
                if (process.HasExited)
                {
                    return null;
                }
            }
            finally
            {
                identity?.Dispose();
            }

            Thread.Sleep(1);
        }

        throw new InvalidOperationException("The launched process did not enter its dedicated process group in time.");
    }

    public bool IsCurrent() => _pidFileDescriptor >= 0 && !HasExited(_pidFileDescriptor);

    public void Signal(int signal)
    {
        ObjectDisposedException.ThrowIf(_pidFileDescriptor < 0, this);
        if (pidfd_send_signal(_pidFileDescriptor, signal, IntPtr.Zero, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 3)
            {
                throw new InvalidOperationException($"Could not send signal {signal} through the test-process pidfd.");
            }
        }
    }

    public void TerminateGroup(TimeSpan gracePeriod)
        => TerminateMatchingProcesses(
            candidate => candidate.ProcessGroupId == ProcessGroupId,
            gracePeriod,
            "process-group");

    public void TerminateTree(TimeSpan gracePeriod)
    {
        if (!IsCurrent())
        {
            return;
        }

        var identities = new List<LinuxProcessIdentity> { this };
        identities.AddRange(CaptureDescendants(ProcessId));
        try
        {
            TerminateOpened(identities, gracePeriod, "process-tree");
        }
        finally
        {
            foreach (var identity in identities.Where(identity => !ReferenceEquals(identity, this)))
            {
                identity.Dispose();
            }
        }
    }

    public void TerminateRemainingGroup()
        => TerminateMatchingProcesses(candidate => candidate.ProcessGroupId == ProcessGroupId, TimeSpan.FromSeconds(5), "remaining process-group");

    public void Dispose()
    {
        var descriptor = Interlocked.Exchange(ref _pidFileDescriptor, -1);
        if (descriptor >= 0)
        {
            _ = close(descriptor);
        }
    }

    private static (int ParentProcessId, int ProcessGroupId, ulong StartTime) ReadIdentity(int processId)
    {
        var stat = File.ReadAllText($"/proc/{processId}/stat");
        var closeParenthesis = stat.LastIndexOf(')');
        if (closeParenthesis < 0)
        {
            throw new InvalidOperationException("Could not parse the launched process identity.");
        }

        var fields = stat[(closeParenthesis + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 20
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentProcessId)
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var processGroupId)
            || !ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var startTime))
        {
            throw new InvalidOperationException("Could not parse the launched process identity.");
        }

        return (parentProcessId, processGroupId, startTime);
    }

    private void TerminateMatchingProcesses(Func<LinuxProcessIdentity, bool> predicate, TimeSpan gracePeriod, string description)
    {
        var identities = new List<LinuxProcessIdentity>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var processId))
            {
                continue;
            }

            try
            {
                var candidate = processId == ProcessId ? this : Capture(processId);
                if (predicate(candidate))
                {
                    identities.Add(candidate);
                }
                else if (!ReferenceEquals(candidate, this))
                {
                    candidate.Dispose();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The pidfd could not be atomically opened and identified.
            }
        }

        try
        {
            TerminateOpened(identities, gracePeriod, description);
        }
        finally
        {
            foreach (var identity in identities.Where(identity => !ReferenceEquals(identity, this)))
            {
                identity.Dispose();
            }
        }
    }

    private static void TerminateOpened(IReadOnlyList<LinuxProcessIdentity> identities, TimeSpan gracePeriod, string description)
    {
        foreach (var identity in identities)
        {
            identity.Signal(15);
        }

        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < gracePeriod && identities.Any(identity => identity.IsCurrent()))
        {
            Thread.Sleep(25);
        }

        foreach (var identity in identities.Where(identity => identity.IsCurrent()))
        {
            identity.Signal(9);
        }

        timer.Restart();
        while (timer.Elapsed < gracePeriod && identities.Any(identity => identity.IsCurrent()))
        {
            Thread.Sleep(25);
        }

        if (identities.Any(identity => identity.IsCurrent()))
        {
            throw new InvalidOperationException($"Could not confirm bounded pidfd termination of the {description}.");
        }
    }

    private static bool HasExited(int descriptor)
    {
        while (true)
        {
            var item = new PollDescriptor { FileDescriptor = descriptor, Events = 1 };
            var result = poll(ref item, 1, 0);
            if (result >= 0)
            {
                return result != 0;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != 4) // EINTR: retry when another test process changes state concurrently.
            {
                throw new IOException($"Could not poll a test-process pidfd (errno {error}).");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int FileDescriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int pidfd_open(int pid, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int pidfd_send_signal(int pidfd, int signal, IntPtr information, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollDescriptor descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
