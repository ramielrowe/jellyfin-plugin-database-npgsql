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
    private readonly LinuxProcessSupervisor _supervisor;
    private readonly HttpClient _httpClient;
    private readonly LinuxProcessIdentity _processIdentity;
    private bool _disposed;
    private bool _logCompleted;

    private JellyfinHostProcess(
        JellyfinHostLayout layout,
        SensitiveDataRedactor redactor,
        LinuxProcessSupervisor supervisor,
        HttpClient httpClient,
        int httpPort,
        LinuxProcessIdentity processIdentity)
    {
        _layout = layout;
        _redactor = redactor;
        _supervisor = supervisor;
        _httpClient = httpClient;
        _processIdentity = processIdentity;
        BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/", UriKind.Absolute);
    }

    public Uri BaseAddress { get; }

    public int ProcessId => _processIdentity.ProcessId;

    public bool HasExited => !_processIdentity.IsCurrent();

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

        var command = new List<string>
        {
            "dotnet", layout.Publish.EntryAssemblyPath,
            "--datadir", layout.DataDirectory,
            "--configdir", layout.ConfigurationDirectory,
            "--cachedir", layout.CacheDirectory,
            "--logdir", layout.LogDirectory,
            "--nowebclient",
            "--nonetchange"
        };
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            command.Add("--ffmpeg");
            command.Add(Path.GetFullPath(ffmpegPath));
        }

        httpPortReservation.ReleaseSocketForLaunch();
        LinuxProcessSupervisor? supervisor = null;
        try
        {
            var logPath = Path.Combine(layout.RunRoot, $"process-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.log");
            supervisor = LinuxProcessSupervisor.Start(
                layout.Publish.DirectoryPath,
                logPath,
                redactor,
                command,
                startInfo =>
                {
                    foreach (var variable in PostgreSqlEnvironmentVariables)
                    {
                        _ = startInfo.Environment.Remove(variable);
                    }
                });
            var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            var httpClient = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/", UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(10)
            };
            return new JellyfinHostProcess(
                layout,
                redactor,
                supervisor,
                httpClient,
                httpPort,
                supervisor.TargetIdentity);
        }
        catch
        {
            if (supervisor is not null)
            {
                supervisor.Terminate(TimeSpan.FromSeconds(15));
                supervisor.Dispose();
            }

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

            if (HasExited)
            {
                _supervisor.Terminate(TimeSpan.FromSeconds(15));
                await CompleteLogAsync().ConfigureAwait(false);
                _layout.CollectSanitizedLogs(_redactor);
                throw new InvalidOperationException(
                    $"Jellyfin exited before becoming ready. See sanitized logs in '{_layout.CollectedLogDirectory}'.");
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
            catch (ListenerNotReadyException) when (_processIdentity.IsCurrent())
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
            await _supervisor.Process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
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
        if (!_supervisor.Process.HasExited)
        {
            _supervisor.Terminate(TimeSpan.FromSeconds(15));
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
            _supervisor.Dispose();
            _layout.RetainOnlySanitizedEvidence(_redactor);
        }
        GC.SuppressFinalize(this);
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
        var status = InspectListener(BaseAddress.Port, out var inode);
        if (status == ListenerStatus.Missing && !HasExited && _processIdentity.IsCurrent())
        {
            throw new ListenerNotReadyException();
        }

        var listenerGroups = status == ListenerStatus.Valid ? GetListenerProcessGroups(inode!) : [];
        if (status == ListenerStatus.Valid
            && listenerGroups.Count == 0
            && !HasExited
            && _processIdentity.IsCurrent())
        {
            throw new ListenerNotReadyException();
        }

        if (HasExited || !_processIdentity.IsCurrent() || !listenerGroups.Contains(_processIdentity.ProcessGroupId))
        {
            throw new InvalidOperationException(
                "The launched Jellyfin process does not own one exact loopback listener on its leased endpoint; refusing to issue an API request. "
                + $"Listener validation: {status}. "
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

        if (InspectListener(port, out var inode) != ListenerStatus.Valid)
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
        if (InspectListener(port, out var inode) != ListenerStatus.Valid)
        {
            return [];
        }

        return GetListenerProcessGroups(inode!);
    }

    private static HashSet<int> GetListenerProcessGroups(string inode)
    {
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

    private static ListenerStatus InspectListener(int port, out string? inode)
    {
        var ipv4 = File.Exists("/proc/net/tcp") ? File.ReadLines("/proc/net/tcp").Skip(1).ToArray() : [];
        var ipv6 = File.Exists("/proc/net/tcp6") ? File.ReadLines("/proc/net/tcp6").Skip(1).ToArray() : [];
        if (TryFindSingleLoopbackListenerInode(ipv4, ipv6, port, out inode))
        {
            return ListenerStatus.Valid;
        }

        return ContainsListeningPort(ipv4, port) || ContainsListeningPort(ipv6, port)
            ? ListenerStatus.Rejected
            : ListenerStatus.Missing;
    }

    public static bool TryFindSingleLoopbackListenerInode(
        IEnumerable<string> ipv4Lines,
        IEnumerable<string> ipv6Lines,
        int port,
        out string? inode)
    {
        ValidatePort(port, nameof(port));
        ArgumentNullException.ThrowIfNull(ipv4Lines);
        ArgumentNullException.ThrowIfNull(ipv6Lines);
        var expectedPort = port.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        var listeners = new List<(string Address, string Inode, bool Ipv6)>();
        AddListeners(ipv4Lines, ipv6: false);
        AddListeners(ipv6Lines, ipv6: true);
        if (listeners.Count != 1)
        {
            inode = null;
            return false;
        }

        var listener = listeners[0];
        var loopback = listener.Ipv6
            ? string.Equals(listener.Address, "00000000000000000000000001000000", StringComparison.OrdinalIgnoreCase)
            : string.Equals(listener.Address, "0100007F", StringComparison.OrdinalIgnoreCase);
        inode = loopback ? listener.Inode : null;
        return loopback;

        void AddListeners(IEnumerable<string> lines, bool ipv6)
        {
            foreach (var line in lines)
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length <= 9 || !string.Equals(fields[3], "0A", StringComparison.Ordinal))
                {
                    continue;
                }

                var endpoint = fields[1].Split(':', StringSplitOptions.None);
                if (endpoint.Length == 2 && string.Equals(endpoint[1], expectedPort, StringComparison.OrdinalIgnoreCase))
                {
                    listeners.Add((endpoint[0], fields[9], ipv6));
                }
            }
        }
    }

    private static bool ContainsListeningPort(IEnumerable<string> lines, int port)
    {
        var expectedPort = port.ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var line in lines)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 3 && string.Equals(fields[3], "0A", StringComparison.Ordinal))
            {
                var endpoint = fields[1].Split(':', StringSplitOptions.None);
                if (endpoint.Length == 2 && string.Equals(endpoint[1], expectedPort, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private enum ListenerStatus
    {
        Missing,
        Valid,
        Rejected
    }

    private sealed class ListenerNotReadyException : InvalidOperationException
    {
    }

    private static async Task CopySanitizedAsync(
        StreamReader source,
        StreamWriter destination,
        SemaphoreSlim outputLock,
        SensitiveDataRedactor redactor,
        string streamName,
        OutputBudget outputBudget)
    {
        var streamingRedactor = redactor.CreateStreamingRedactor();
        var buffer = new char[4096];
        var wrotePrefix = false;
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer).ConfigureAwait(false);
                var sanitized = streamingRedactor.Redact(buffer.AsSpan(0, count), final: count == 0);
                if (!wrotePrefix)
                {
                    sanitized = $"[{streamName}] " + sanitized;
                    wrotePrefix = true;
                }

                if (sanitized.Length != 0 && outputBudget.TryReserve(Encoding.UTF8.GetByteCount(sanitized)))
                {
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

                if (count == 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Bounded shutdown closes inherited pipes retained by descendants.
        }
    }

    public static async Task CopySanitizedOutputAsync(
        StreamReader source,
        StreamWriter destination,
        SensitiveDataRedactor redactor,
        int maximumBytes,
        string streamName = "test")
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(redactor);
        if (maximumBytes < 1 || maximumBytes > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        using var outputLock = new SemaphoreSlim(1, 1);
        await CopySanitizedAsync(source, destination, outputLock, redactor, streamName, new OutputBudget(maximumBytes)).ConfigureAwait(false);
        await destination.FlushAsync().ConfigureAwait(false);
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
                    if (Interlocked.CompareExchange(ref _remainingBytes, 0, remaining) != remaining)
                    {
                        continue;
                    }

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

        if (!_supervisor.Process.HasExited)
        {
            throw new InvalidOperationException("Cannot complete the supervised process log while the supervisor is running.");
        }

        if (await Task.WhenAny(_supervisor.StandardError, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != _supervisor.StandardError)
        {
            throw new InvalidOperationException("The process-supervisor diagnostic stream did not close after bounded shutdown.");
        }

        var diagnostic = await _supervisor.StandardError.ConfigureAwait(false);
        if (_supervisor.Process.ExitCode == 125)
        {
            throw new InvalidOperationException("The Linux process supervisor failed: " + _redactor.Redact(diagnostic));
        }

        if (_supervisor.Process.ExitCode != 0 && !_supervisor.WasTerminationRequested)
        {
            throw new InvalidOperationException(
                $"The supervised Jellyfin host exited unexpectedly with status {_supervisor.Process.ExitCode}: "
                + _redactor.Redact(diagnostic));
        }

        _logCompleted = true;
    }
}
