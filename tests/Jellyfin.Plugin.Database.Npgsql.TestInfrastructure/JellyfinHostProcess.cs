using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private readonly LinuxProcessIdentity _processIdentity;
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
        int httpPort,
        LinuxProcessIdentity processIdentity)
    {
        _layout = layout;
        _redactor = redactor;
        _process = process;
        _httpClient = httpClient;
        _processLog = processLog;
        _standardOutput = standardOutput;
        _standardError = standardError;
        _processIdentity = processIdentity;
        BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/", UriKind.Absolute);
    }

    public Uri BaseAddress { get; }

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    public static JellyfinHostProcess Start(
        JellyfinHostLayout layout,
        LoopbackPortReservation httpPortReservation,
        SensitiveDataRedactor redactor,
        string? ffmpegPath = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(httpPortReservation);
        ArgumentNullException.ThrowIfNull(redactor);
        var httpPort = httpPortReservation.Port;
        ValidatePort(httpPort, nameof(httpPortReservation));
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The pristine-host harness requires Linux process-group and socket identity checks.");
        }

        var startInfo = new ProcessStartInfo("setsid")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = layout.Publish.DirectoryPath
        };
        startInfo.ArgumentList.Add("--wait");
        startInfo.ArgumentList.Add("dotnet");
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

        httpPortReservation.ReleaseSocketForLaunch();
        var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Could not start the integrity-checked Jellyfin publish.");
            }

            started = true;
            var processIdentity = LinuxProcessIdentity.AwaitDedicatedGroup(process, TimeSpan.FromSeconds(1))
                ?? throw new InvalidOperationException("Jellyfin exited before entering its dedicated test process group.");

            var logPath = Path.Combine(layout.RunRoot, $"process-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
            var processLog = new StreamWriter(SecureFileSystem.CreateFile(logPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            var outputLock = new SemaphoreSlim(1, 1);
            var outputBudget = new OutputBudget(16 * 1024 * 1024);
            var standardOutput = CopySanitizedAsync(process.StandardOutput, processLog, outputLock, redactor, "stdout", outputBudget);
            var standardError = CopySanitizedAsync(process.StandardError, processLog, outputLock, redactor, "stderr", outputBudget);
            var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
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
                httpPort,
                processIdentity);
        }
        catch
        {
            if (started)
            {
                ExternalCommand.TryTerminate(process);
            }

            process.Dispose();
            layout.RetainOnlySanitizedEvidence(redactor);
            throw;
        }
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
                using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "health"), linkedSource.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK
                    && string.Equals(
                        (await ReadBoundedResponseAsync(response, linkedSource.Token).ConfigureAwait(false)).Trim(),
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
            catch (InvalidOperationException) when (
                _processIdentity.IsCurrent()
                && GetListenerProcessGroups(BaseAddress.Port).Count == 0)
            {
                // The verified process is alive but has not opened its listener yet.
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
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, relativePath), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PostJsonAsync(
        string relativePath,
        string json,
        string? authorization,
        CancellationToken cancellationToken)
    {
        ValidateRelativePath(relativePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopGracefullyAsync(string accessToken, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "System/Shutdown");
        request.Headers.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"" + accessToken + "\"");
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await WaitForExitAsync(timeout, cancellationToken).ConfigureAwait(false);
        await CompleteLogAsync().ConfigureAwait(false);
        _layout.CollectSanitizedLogs(_redactor);
        await _layout.Publish.VerifyUnchangedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Jellyfin exit timeout must be at most five minutes.");
        }

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
            ExternalCommand.TryTerminate(_process, _processIdentity);
        }
        else
        {
            _processIdentity.TerminateRemainingGroup();
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
        try
        {
            await TerminateAsync().ConfigureAwait(false);
            await _layout.Publish.VerifyUnchangedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _httpClient.Dispose();
            _process.Dispose();
            _layout.RetainOnlySanitizedEvidence(_redactor);
        }
        GC.SuppressFinalize(this);
    }

    private static void AddArgument(ProcessStartInfo startInfo, string name, string value)
    {
        startInfo.ArgumentList.Add(name);
        startInfo.ArgumentList.Add(value);
    }

    private static void ValidatePort(int port, string parameterName)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(parameterName, "A TCP port must be a decimal value from 1 through 65535.");
        }
    }

    private void EnsureOwnsEndpoint()
    {
        var listenerGroups = GetListenerProcessGroups(BaseAddress.Port);
        if (_process.HasExited
            || !_processIdentity.IsCurrent()
            || !listenerGroups.Contains(_processIdentity.ProcessGroupId))
        {
            throw new InvalidOperationException(
                "The launched Jellyfin process no longer owns its leased endpoint; refusing to issue an API request. "
                + $"Expected process group {_processIdentity.ProcessGroupId}; observed listener groups: "
                + (listenerGroups.Count == 0 ? "none" : string.Join(',', listenerGroups)) + ".");
        }
    }

    public static bool ProcessOwnsListeningPort(int processId, int port)
    {
        ValidatePort(port, nameof(port));
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Process-owned listener verification currently requires Linux /proc.");
        }

        var inode = FindLoopbackListenerInode(port);
        if (inode is null)
        {
            return false;
        }

        try
        {
            foreach (var descriptor in Directory.EnumerateFiles($"/proc/{processId}/fd"))
            {
                var target = new FileInfo(descriptor).LinkTarget;
                if (string.Equals(target, "socket:[" + inode + "]", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return false;
        }

        return false;
    }

    private static HashSet<int> GetListenerProcessGroups(int port)
    {
        var inode = FindLoopbackListenerInode(port);
        if (inode is null)
        {
            return [];
        }

        var processGroups = new HashSet<int>();
        foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(processDirectory), out var processId))
            {
                continue;
            }

            try
            {
                var processIdentity = LinuxProcessIdentity.Capture(processId);
                foreach (var descriptor in Directory.EnumerateFiles(Path.Combine(processDirectory, "fd")))
                {
                    if (string.Equals(new FileInfo(descriptor).LinkTarget, "socket:[" + inode + "]", StringComparison.Ordinal))
                    {
                        _ = processGroups.Add(processIdentity.ProcessGroupId);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or InvalidOperationException)
            {
                // Processes may exit while /proc is inspected.
            }
        }

        return processGroups;
    }

    private static string? FindLoopbackListenerInode(int port)
    {
        var expectedPort = port.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table))
            {
                continue;
            }

            foreach (var line in File.ReadLines(table).Skip(1))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 9
                    && fields[1].EndsWith(":" + expectedPort, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fields[3], "0A", StringComparison.Ordinal))
                {
                    return fields[9];
                }
            }
        }

        return null;
    }

    private static async Task CopySanitizedAsync(
        StreamReader source,
        StreamWriter destination,
        SemaphoreSlim outputLock,
        SensitiveDataRedactor redactor,
        string streamName,
        OutputBudget outputBudget)
    {
        string? line;
        try
        {
            while ((line = await source.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var sanitized = $"[{streamName}] {redactor.Redact(line)}{Environment.NewLine}";
                if (!outputBudget.TryReserve(Encoding.UTF8.GetByteCount(sanitized)))
                {
                    continue;
                }

                await outputLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await destination.WriteAsync(sanitized).ConfigureAwait(false);
                }
                finally
                {
                    outputLock.Release();
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Bounded shutdown closes inherited pipes retained by descendants.
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || request.RequestUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Only relative requests to the verified Jellyfin endpoint are allowed.");
        }

        ValidateRelativePath(request.RequestUri.OriginalString);
        EnsureOwnsEndpoint();
        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureOwnsEndpoint();
            if (response.Headers.Location is { IsAbsoluteUri: true })
            {
                throw new InvalidOperationException("The verified Jellyfin endpoint attempted an absolute redirect.");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task<string> ReadBoundedResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        const int MaximumResponseBytes = 1024 * 1024;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (buffer.Length + count > MaximumResponseBytes)
            {
                throw new InvalidOperationException("The Jellyfin test response exceeded the bounded one-megabyte limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void ValidateRelativePath(string relativePath)
    {
        if (!IsSafeRelativeRequestTarget(relativePath))
        {
            throw new InvalidOperationException("Only a relative path without an authority may target the verified Jellyfin endpoint.");
        }
    }

    public static bool IsSafeRelativeRequestTarget(string relativePath)
        => !string.IsNullOrWhiteSpace(relativePath)
            && !relativePath.StartsWith("/", StringComparison.Ordinal)
            && !relativePath.StartsWith("\\", StringComparison.Ordinal)
            && Uri.TryCreate(relativePath, UriKind.Relative, out _);

    private sealed class OutputBudget(int maximumBytes)
    {
        private int _remainingBytes = maximumBytes;

        public bool TryReserve(int bytes)
        {
            while (true)
            {
                var remaining = Volatile.Read(ref _remainingBytes);
                if (bytes > remaining)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _remainingBytes, remaining - bytes, remaining) == remaining)
                {
                    return true;
                }
            }
        }
    }

    private async Task CompleteLogAsync()
    {
        if (_logCompleted)
        {
            return;
        }

        var readers = Task.WhenAll(_standardOutput, _standardError);
        if (await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != readers)
        {
            _process.StandardOutput.Dispose();
            _process.StandardError.Dispose();
            if (await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != readers)
            {
                throw new InvalidOperationException("Jellyfin output readers did not terminate after their bounded pipe shutdown.");
            }
        }
        else
        {
            await readers.ConfigureAwait(false);
        }
        await _processLog.FlushAsync().ConfigureAwait(false);
        _processLog.Dispose();
        _logCompleted = true;
    }
}
