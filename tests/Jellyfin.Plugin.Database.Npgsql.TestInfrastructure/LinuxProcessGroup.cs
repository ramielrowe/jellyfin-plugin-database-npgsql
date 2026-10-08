using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

internal sealed record LinuxProcessIdentity(int ProcessId, int ParentProcessId, int ProcessGroupId, ulong StartTime)
{
    public static LinuxProcessIdentity Capture(int processId)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Safe test-process identity checks require Linux /proc.");
        }

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

        return new LinuxProcessIdentity(processId, parentProcessId, processGroupId, startTime);
    }

    public static IReadOnlyList<LinuxProcessIdentity> CaptureDescendants(int rootProcessId)
    {
        var all = new List<LinuxProcessIdentity>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var processId))
            {
                continue;
            }

            try
            {
                all.Add(Capture(processId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A process exited during enumeration.
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

        return descendants;
    }

    public static LinuxProcessIdentity? AwaitDedicatedGroup(Process process, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return null;
            }

            try
            {
                var identity = Capture(process.Id);
                if (identity.ProcessGroupId == identity.ProcessId)
                {
                    return identity;
                }
            }
            catch (IOException)
            {
                if (process.HasExited)
                {
                    return null;
                }
            }

            Thread.Sleep(1);
        }

        throw new InvalidOperationException("The launched process did not enter its dedicated process group in time.");
    }

    public bool IsCurrent()
    {
        try
        {
            var current = Capture(ProcessId);
            return current.ProcessId == ProcessId
                && current.ProcessGroupId == ProcessGroupId
                && current.StartTime == StartTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public void TerminateGroup(TimeSpan gracePeriod)
    {
        if (!IsCurrent() || ProcessGroupId != ProcessId)
        {
            throw new InvalidOperationException("Refusing to signal a process group whose leader identity changed.");
        }

        if (kill(-ProcessGroupId, 15) != 0 && Marshal.GetLastPInvokeError() != 3)
        {
            throw new InvalidOperationException("Could not send TERM to the verified test process group.");
        }

        var deadline = DateTime.UtcNow + gracePeriod;
        while (DateTime.UtcNow < deadline && IsCurrent())
        {
            System.Threading.Thread.Sleep(50);
        }

        if (IsCurrent())
        {
            if (kill(-ProcessGroupId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
            {
                throw new InvalidOperationException("Could not send KILL to the verified test process group.");
            }
        }
    }

    public void TerminateRemainingGroup()
    {
        var members = new List<LinuxProcessIdentity>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var processId))
            {
                continue;
            }

            try
            {
                var candidate = Capture(processId);
                if (candidate.ProcessGroupId == ProcessGroupId)
                {
                    members.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // A process exited during enumeration.
            }
        }

        if (members.Count == 0)
        {
            return;
        }

        if (members.Exists(member => member.ProcessId == ProcessGroupId && member.StartTime != StartTime))
        {
            throw new InvalidOperationException("Refusing to signal a process group whose numeric ID was reused.");
        }

        if (kill(-ProcessGroupId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
        {
            throw new InvalidOperationException("Could not terminate remaining verified test-process descendants.");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
