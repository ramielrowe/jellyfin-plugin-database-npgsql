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

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
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
            TryTerminate(process);
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

    public static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the checks.
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
                destination.Append(buffer, 0, count);
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
        _ = await Task.WhenAny(readers, Task.Delay(TerminationGracePeriod)).ConfigureAwait(false);
    }
}
