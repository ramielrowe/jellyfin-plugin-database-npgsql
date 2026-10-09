using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed record ExternalCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public void EnsureSuccess(string description, SensitiveDataRedactor redactor)
    {
        if (ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{description} failed with exit code {ExitCode}: {redactor.Redact(StandardError)}");
        }
    }
}

public static class ExternalCommand
{
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(5);
    private const int MaximumCapturedCharacters = 1024 * 1024;

    public static async Task<ExternalCommandResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "External command timeout must be between zero and ten minutes.");
        }

        var startInfo = new ProcessStartInfo(OperatingSystem.IsLinux() ? "setsid" : fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        if (OperatingSystem.IsLinux())
        {
            startInfo.ArgumentList.Add("--wait");
            startInfo.ArgumentList.Add(fileName);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start required test command '{fileName}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not start required test command '{fileName}'. Install it or disable the opt-in infrastructure test.",
                ex);
        }

        LinuxProcessIdentity? identity = null;
        if (OperatingSystem.IsLinux())
        {
            identity = LinuxProcessIdentity.AwaitDedicatedGroup(process, TimeSpan.FromSeconds(1));
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readStdout = ReadAsync(process.StandardOutput, stdout);
        var readStderr = ReadAsync(process.StandardError, stderr);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process, identity);
            await DrainOutputBoundedAsync(process, readStdout, readStderr).ConfigureAwait(false);
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Test command '{fileName}' exceeded its {timeout.TotalSeconds:F0}-second timeout.");
            }

            throw;
        }

        await DrainOutputBoundedAsync(process, readStdout, readStderr).ConfigureAwait(false);
        return new ExternalCommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    internal static void TryTerminate(Process process, LinuxProcessIdentity? identity = null)
    {
        if (!process.HasExited)
        {
            if (OperatingSystem.IsLinux())
            {
                var verifiedIdentity = identity ?? LinuxProcessIdentity.Capture(process.Id);
                verifiedIdentity.TerminateTree(TerminationGracePeriod);
            }
            else
            {
                process.Kill(entireProcessTree: true);
            }

            if (!process.WaitForExit(10_000))
            {
                throw new InvalidOperationException("The external test command could not be confirmed terminated.");
            }
        }
    }

    private static async Task ReadAsync(System.IO.StreamReader reader, StringBuilder destination)
    {
        var buffer = new char[4096];
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                var remaining = MaximumCapturedCharacters - destination.Length;
                if (remaining > 0)
                {
                    destination.Append(buffer, 0, Math.Min(count, remaining));
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.IO.IOException)
        {
            // A detached descendant can keep an inherited pipe open forever.
            // Bounded shutdown closes our reader after the command itself dies.
        }
    }

    private static async Task DrainOutputBoundedAsync(Process process, Task stdout, Task stderr)
    {
        var readers = Task.WhenAll(stdout, stderr);
        if (await Task.WhenAny(readers, Task.Delay(TerminationGracePeriod)).ConfigureAwait(false) == readers)
        {
            await readers.ConfigureAwait(false);
            return;
        }

        process.StandardOutput.Dispose();
        process.StandardError.Dispose();
        if (await Task.WhenAny(readers, Task.Delay(TerminationGracePeriod)).ConfigureAwait(false) != readers)
        {
            throw new InvalidOperationException("External-command output readers did not terminate after bounded pipe shutdown.");
        }
    }
}
