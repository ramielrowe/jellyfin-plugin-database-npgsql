using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class JellyfinHostProcess : IAsyncDisposable
{
    private static readonly string[] PostgreSqlEnvironmentVariables =
    {
        "PGUSER", "PGPASSWORD", "PGPASSFILE", "PGSSLCERT", "PGSSLKEY", "PGSSLROOTCERT",
        "PGCLIENTENCODING", "PGTZ", "PGOPTIONS", "PGTARGETSESSIONATTRS", "PGSSLNEGOTIATION",
        "PGGSSENCMODE", "PGREQUIREAUTH", "PGAPPNAME"
    };

    private readonly JellyfinHostLayout _layout;
    private readonly SensitiveDataRedactor _redactor;
    private readonly Process _process;
    private readonly HttpClient _httpClient;
    private readonly StreamWriter _processLog;
    private readonly Task _standardOutput;
    private readonly Task _standardError;
    private bool _disposed;
    private bool _logCompleted;

    private JellyfinHostProcess(
        JellyfinHostLayout layout,
        SensitiveDataRedactor redactor,
        Process process,
        HttpClient httpClient,
        StreamWriter processLog,
        Task standardOutput,
        Task standardError,
        int httpPort)
    {
        _layout = layout;
        _redactor = redactor;
        _process = process;
        _httpClient = httpClient;
        _processLog = processLog;
        _standardOutput = standardOutput;
        _standardError = standardError;
        BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/", UriKind.Absolute);
    }

    public Uri BaseAddress { get; }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public static JellyfinHostProcess Start(
        JellyfinHostLayout layout,
        int httpPort,
        SensitiveDataRedactor redactor,
        string? ffmpegPath = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(redactor);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = layout.Publish.DirectoryPath
        };
        startInfo.ArgumentList.Add(layout.Publish.EntryAssemblyPath);
        AddArgument(startInfo, "--datadir", layout.DataDirectory);
        AddArgument(startInfo, "--configdir", layout.ConfigurationDirectory);
        AddArgument(startInfo, "--cachedir", layout.CacheDirectory);
        AddArgument(startInfo, "--logdir", layout.LogDirectory);
        startInfo.ArgumentList.Add("--nowebclient");
        startInfo.ArgumentList.Add("--nonetchange");
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            AddArgument(startInfo, "--ffmpeg", Path.GetFullPath(ffmpegPath));
        }

        foreach (var variable in PostgreSqlEnvironmentVariables)
        {
            _ = startInfo.Environment.Remove(variable);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the integrity-checked Jellyfin publish.");
        }

        var logPath = Path.Combine(layout.RunRoot, $"process-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
        var processLog = new StreamWriter(logPath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        var outputLock = new SemaphoreSlim(1, 1);
        var standardOutput = CopySanitizedAsync(process.StandardOutput, processLog, outputLock, redactor, "stdout");
        var standardError = CopySanitizedAsync(process.StandardError, processLog, outputLock, redactor, "stderr");
        var handler = new SocketsHttpHandler { UseProxy = false };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(10)
        };
        return new JellyfinHostProcess(
            layout,
            redactor,
            process,
            httpClient,
            processLog,
            standardOutput,
            standardError,
            httpPort);
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Jellyfin readiness timeout must be at most five minutes.");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        while (true)
        {
            if (linkedSource.IsCancellationRequested)
            {
                await TerminateAsync().ConfigureAwait(false);
                if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Jellyfin did not become ready within {timeout.TotalSeconds:F0} seconds.");
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_process.HasExited)
            {
                await CompleteLogAsync().ConfigureAwait(false);
                _layout.CollectSanitizedLogs(_redactor);
                throw new InvalidOperationException(
                    $"Jellyfin exited with code {_process.ExitCode} before becoming ready. See sanitized logs in '{_layout.CollectedLogDirectory}'.");
            }

            try
            {
                using var response = await _httpClient.GetAsync("health", linkedSource.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK
                    && string.Equals(
                        (await response.Content.ReadAsStringAsync(linkedSource.Token).ConfigureAwait(false)).Trim(),
                        "Healthy",
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The loopback listener is not ready yet.
            }
            catch (TaskCanceledException) when (!linkedSource.IsCancellationRequested)
            {
                // One request timed out; the bounded readiness deadline still applies.
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await TerminateAsync().ConfigureAwait(false);
                throw new TimeoutException($"Jellyfin did not become ready within {timeout.TotalSeconds:F0} seconds.");
            }
            catch (OperationCanceledException)
            {
                await TerminateAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    public async Task<string> GetStringAsync(string relativePath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PostJsonAsync(
        string relativePath,
        string json,
        string? authorization,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopGracefullyAsync(string accessToken, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "System/Shutdown");
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"" + accessToken + "\"");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false);
        await CompleteLogAsync().ConfigureAwait(false);
        _layout.CollectSanitizedLogs(_redactor);
        await _layout.Publish.VerifyUnchangedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateAsync().ConfigureAwait(false);
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Jellyfin did not stop within the configured timeout and was terminated.");
            }

            throw;
        }
    }

    public async Task TerminateAsync()
    {
        if (!_process.HasExited)
        {
            ExternalCommand.TryTerminate(_process);
        }

        await CompleteLogAsync().ConfigureAwait(false);
        _layout.CollectSanitizedLogs(_redactor);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await TerminateAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        _process.Dispose();
        await _layout.Publish.VerifyUnchangedAsync(CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static async Task CopySanitizedAsync(
        StreamReader source,
        StreamWriter destination,
        SemaphoreSlim outputLock,
        SensitiveDataRedactor redactor,
        string streamName)
    {
        string? line;
        while ((line = await source.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            await outputLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await destination.WriteLineAsync($"[{streamName}] {redactor.Redact(line)}").ConfigureAwait(false);
            }
            finally
            {
                outputLock.Release();
            }
        }
    }

    private async Task CompleteLogAsync()
    {
        if (_logCompleted)
        {
            return;
        }

        await Task.WhenAll(_standardOutput, _standardError).ConfigureAwait(false);
        await _processLog.FlushAsync().ConfigureAwait(false);
        _processLog.Dispose();
        _logCompleted = true;
    }
}
