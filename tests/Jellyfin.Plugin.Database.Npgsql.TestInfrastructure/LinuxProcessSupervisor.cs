using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

internal sealed class LinuxProcessSupervisor : IDisposable
{
    private readonly string _identityPath;
    private bool _disposed;

    private LinuxProcessSupervisor(
        Process process,
        LinuxProcessIdentity supervisorIdentity,
        LinuxProcessIdentity targetIdentity,
        string identityPath,
        Task<string> standardError)
    {
        Process = process;
        SupervisorIdentity = supervisorIdentity;
        TargetIdentity = targetIdentity;
        _identityPath = identityPath;
        StandardError = standardError;
    }

    public Process Process { get; }

    public LinuxProcessIdentity SupervisorIdentity { get; }

    public LinuxProcessIdentity TargetIdentity { get; }

    public Task<string> StandardError { get; }

    public static LinuxProcessSupervisor Start(
        string workingDirectory,
        string logPath,
        SensitiveDataRedactor redactor,
        IReadOnlyList<string> command,
        Action<ProcessStartInfo>? configureEnvironment = null)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Safe test-process supervision requires Linux subreapers and /proc.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Count == 0)
        {
            throw new ArgumentException("A supervised command is required.", nameof(command));
        }

        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var supervisorScript = Path.Combine(repositoryRoot, "tests", "process-supervisor.py");
        if (!File.Exists(supervisorScript))
        {
            throw new InvalidOperationException("The checked-in Linux process supervisor is missing.");
        }

        var identityPath = Path.Combine(Path.GetDirectoryName(logPath)!, ".supervisor-" + Guid.NewGuid().ToString("N") + ".identity");
        var secretsPath = Path.Combine(Path.GetDirectoryName(logPath)!, ".supervisor-" + Guid.NewGuid().ToString("N") + ".secrets");
        SecureFileSystem.WriteAllTextAtomic(secretsPath, string.Join('\0', redactor.SnapshotSecrets()) + "\0");

        var startInfo = new ProcessStartInfo("python3")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in new[]
        {
            supervisorScript,
            "--identity-file", identityPath,
            "--log-file", logPath,
            "--secrets-file", secretsPath,
            "--working-directory", workingDirectory,
            "--"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        configureEnvironment?.Invoke(startInfo);
        var process = new Process { StartInfo = startInfo };
        var started = false;
        LinuxProcessIdentity? supervisorIdentity = null;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start the checked-in Linux process supervisor.");
            }

            started = true;
            supervisorIdentity = LinuxProcessIdentity.Capture(process.Id);
            var standardError = ReadBoundedErrorAsync(process.StandardError);
            _ = DrainUnusedOutputAsync(process.StandardOutput);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!File.Exists(identityPath) && DateTime.UtcNow < deadline && !process.HasExited)
            {
                Thread.Sleep(10);
            }

            if (!File.Exists(identityPath))
            {
                var diagnostic = standardError.Wait(TimeSpan.FromSeconds(2)) ? standardError.Result : string.Empty;
                throw new InvalidOperationException("The Linux process supervisor failed closed during initialization. " + redactor.Redact(diagnostic));
            }

            var fields = File.ReadAllText(identityPath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6
                || fields[0] != "v1"
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var reportedSupervisorPid)
                || !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var reportedSupervisorStart)
                || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var targetPid)
                || !ulong.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var targetStart)
                || !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out var targetGroup)
                || reportedSupervisorPid != supervisorIdentity.ProcessId
                || reportedSupervisorStart != supervisorIdentity.StartTime)
            {
                throw new InvalidOperationException("The Linux process supervisor returned a malformed or mismatched identity record.");
            }

            LinuxProcessIdentity targetIdentity;
            try
            {
                targetIdentity = LinuxProcessIdentity.Capture(targetPid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // The stable supervisor may already be reaping a root which intentionally exited first.
                targetIdentity = new LinuxProcessIdentity(targetPid, supervisorIdentity.ProcessId, targetGroup, targetStart);
            }

            if (targetIdentity.StartTime != targetStart
                || targetIdentity.ProcessGroupId != targetGroup
                || targetIdentity.ProcessId != targetIdentity.ProcessGroupId)
            {
                throw new InvalidOperationException("The supervised target identity changed during initialization.");
            }

            return new LinuxProcessSupervisor(process, supervisorIdentity, targetIdentity, identityPath, standardError);
        }
        catch
        {
            if (started && !process.HasExited)
            {
                supervisorIdentity ??= LinuxProcessIdentity.Capture(process.Id);
                TrySignalVerified(process, supervisorIdentity, 15);
                _ = process.WaitForExit(10_000);
            }

            process.Dispose();
            TryDelete(identityPath);
            TryDelete(secretsPath);
            throw;
        }
    }

    public void Terminate(TimeSpan timeout)
    {
        if (!Process.HasExited)
        {
            if (!SupervisorIdentity.IsCurrent())
            {
                throw new InvalidOperationException("Refusing to signal a process supervisor whose PID identity changed.");
            }

            TrySignalVerified(Process, SupervisorIdentity, 15);
        }

        if (!Process.WaitForExit((int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)))
        {
            if (!SupervisorIdentity.IsCurrent())
            {
                throw new InvalidOperationException("The process supervisor did not stop and its PID identity is no longer provable.");
            }

            TrySignalVerified(Process, SupervisorIdentity, 9);
            if (!Process.WaitForExit(10_000))
            {
                throw new InvalidOperationException("The Linux process supervisor could not be confirmed terminated.");
            }
        }
    }

    public static bool IdentityMatches(int processId, ulong expectedStart)
    {
        try
        {
            return LinuxProcessIdentity.Capture(processId).StartTime == expectedStart;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!Process.HasExited)
        {
            Terminate(TimeSpan.FromSeconds(15));
        }

        TryDelete(_identityPath);
        Process.Dispose();
    }

    private static void TrySignalVerified(Process process, LinuxProcessIdentity identity, int signal)
    {
        if (!identity.IsCurrent())
        {
            throw new InvalidOperationException("Refusing to signal a process whose PID identity changed.");
        }

        identity.Signal(signal);
    }

    private static async Task<string> ReadBoundedErrorAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        var output = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToString();
            }

            var remaining = 64 * 1024 - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(remaining, count));
            }
        }
    }

    private static async Task DrainUnusedOutputAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer).ConfigureAwait(false) != 0)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
