using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class HostTestArtifactScope : IDisposable
{
    public const string ArtifactRootEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_ARTIFACT_ROOT";
    public const int MaximumRetainedEvidenceBytes = 8 * 1024 * 1024;
    private const int MaximumRetainedFileBytes = 2 * 1024 * 1024;
    private const int MaximumFailureSummaryCharacters = 16 * 1024;
    private const string CaseMarkerFileName = ".jellyfin-npgsql-test-case";
    private const string EvidenceMarkerFileName = ".jellyfin-npgsql-retained-evidence";
    private readonly string _caseToken;
    private readonly string _casesRoot;
    private bool _disposed;

    private HostTestArtifactScope(string artifactRoot, string casesRoot, string caseDirectory, string caseToken)
    {
        ArtifactRoot = artifactRoot;
        _casesRoot = casesRoot;
        CaseDirectory = caseDirectory;
        _caseToken = caseToken;
    }

    public string ArtifactRoot { get; }

    public string CaseDirectory { get; }

    public string? RetainedEvidenceDirectory { get; private set; }

    public long RetainedEvidenceBytes { get; private set; }

    public static HostTestArtifactScope Create(string caseName, string? artifactRootOverride = null)
    {
        var safeCaseName = SanitizeName(caseName);
        var configuredRoot = artifactRootOverride
            ?? Environment.GetEnvironmentVariable(ArtifactRootEnvironmentVariable);
        var artifactRoot = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(PluginRepository.FindRoot(AppContext.BaseDirectory), ".artifacts", "host-tests")
            : Path.GetFullPath(configuredRoot);
        artifactRoot = Path.GetFullPath(artifactRoot);
        var casesRoot = Path.Combine(artifactRoot, "cases");
        SecureFileSystem.CreateDirectory(artifactRoot);
        SecureFileSystem.CreateDirectory(casesRoot);

        var caseToken = Guid.NewGuid().ToString("N");
        var caseDirectory = Path.GetFullPath(Path.Combine(casesRoot, safeCaseName + "-" + caseToken));
        EnsureChildPath(casesRoot, caseDirectory, "case directory");
        SecureFileSystem.CreateDirectory(caseDirectory);
        try
        {
            SecureFileSystem.WriteAllTextAtomic(
                Path.Combine(caseDirectory, CaseMarkerFileName),
                caseToken + Environment.NewLine);
            return new HostTestArtifactScope(artifactRoot, casesRoot, caseDirectory, caseToken);
        }
        catch
        {
            Directory.Delete(caseDirectory, recursive: true);
            throw;
        }
    }

    public void RetainFailureEvidence(Exception exception, SensitiveDataRedactor redactor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(redactor);
        ValidateCaseOwnership();
        if (RetainedEvidenceDirectory is not null)
        {
            throw new InvalidOperationException("Failure evidence has already been retained for this test case.");
        }

        var evidenceRoot = Path.Combine(ArtifactRoot, "retained-evidence");
        SecureFileSystem.CreateDirectory(evidenceRoot);
        var evidenceDirectory = Path.GetFullPath(
            Path.Combine(evidenceRoot, "failed-" + Path.GetFileName(CaseDirectory)));
        EnsureChildPath(evidenceRoot, evidenceDirectory, "retained-evidence directory");
        SecureFileSystem.CreateDirectory(evidenceDirectory);
        SecureFileSystem.WriteAllTextAtomic(
            Path.Combine(evidenceDirectory, EvidenceMarkerFileName),
            _caseToken + Environment.NewLine);

        try
        {
            var summary = redactor.Redact(exception.ToString());
            if (summary.Length > MaximumFailureSummaryCharacters)
            {
                summary = summary[..MaximumFailureSummaryCharacters] + Environment.NewLine + "[diagnostic truncated]";
            }

            EnsureSafeEvidence(summary, redactor);
            SecureFileSystem.WriteAllTextAtomic(Path.Combine(evidenceDirectory, "failure.txt"), summary);
            var retainedBytes = checked((int)new FileInfo(Path.Combine(evidenceDirectory, "failure.txt")).Length);

            foreach (var source in Directory.EnumerateFiles(CaseDirectory, "*", SearchOption.AllDirectories)
                         .Where(IsCollectedLog)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var remaining = Math.Min(MaximumRetainedFileBytes, MaximumRetainedEvidenceBytes - retainedBytes);
                if (remaining <= 0)
                {
                    break;
                }

                var relative = Path.GetRelativePath(CaseDirectory, source);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))).ToLowerInvariant()[..16];
                var safeName = SanitizeName(Path.GetFileName(source));
                var destination = Path.Combine(evidenceDirectory, hash + "-" + safeName);
                retainedBytes += CopySanitizedBounded(source, destination, redactor, remaining);
            }

            RetainedEvidenceDirectory = evidenceDirectory;
            RetainedEvidenceBytes = Directory.EnumerateFiles(evidenceDirectory, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFileName(path), EvidenceMarkerFileName, StringComparison.Ordinal))
                .Sum(path => new FileInfo(path).Length);
            Console.Error.WriteLine(
                $"Retained sanitized host-test diagnostics at '{RetainedEvidenceDirectory}' ({RetainedEvidenceBytes} bytes). The copied host and case scratch directory will be deleted.");
        }
        catch
        {
            if (Directory.Exists(evidenceDirectory))
            {
                Directory.Delete(evidenceDirectory, recursive: true);
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (Directory.Exists(CaseDirectory))
        {
            ValidateCaseOwnership();
            Directory.Delete(CaseDirectory, recursive: true);
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static int CopySanitizedBounded(
        string source,
        string destination,
        SensitiveDataRedactor redactor,
        int maximumBytes)
    {
        var streamingRedactor = redactor.CreateStreamingRedactor();
        var privateKeyMatcher = new StreamingTextMatcher("PRIVATE KEY-----");
        var secretMatchers = redactor.CreateSecretMatchers();
        var written = 0;
        using var input = new StreamReader(
            new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: false);
        using var outputStream = SecureFileSystem.CreateFile(destination);
        using var output = new StreamWriter(outputStream, new UTF8Encoding(false), 4096, leaveOpen: false);
        var buffer = new char[4096];
        while (true)
        {
            var count = input.Read(buffer, 0, buffer.Length);
            var final = count == 0;
            if (privateKeyMatcher.Contains(buffer.AsSpan(0, count), final))
            {
                throw new InvalidOperationException("A private key was detected in host-test evidence; no failure artifacts were retained.");
            }

            var sanitized = streamingRedactor.Redact(buffer.AsSpan(0, count), final);
            if (secretMatchers.Any(matcher => matcher.Contains(sanitized, final)))
            {
                throw new InvalidOperationException("A generated secret survived host-test evidence redaction; no failure artifacts were retained.");
            }

            var remaining = maximumBytes - written;
            if (remaining <= 0)
            {
                break;
            }

            var fittingLength = FindFittingCharacterCount(sanitized, remaining);
            if (fittingLength != 0)
            {
                output.Write(sanitized.AsSpan(0, fittingLength));
                written += Encoding.UTF8.GetByteCount(sanitized.AsSpan(0, fittingLength));
            }

            if (final || fittingLength != sanitized.Length)
            {
                break;
            }
        }

        output.Flush();
        outputStream.Flush(flushToDisk: true);
        return written;
    }

    private static int FindFittingCharacterCount(string value, int maximumBytes)
    {
        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var candidate = low + ((high - low + 1) / 2);
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, candidate)) <= maximumBytes)
            {
                low = candidate;
            }
            else
            {
                high = candidate - 1;
            }
        }

        if (low < value.Length
            && low > 0
            && char.IsHighSurrogate(value[low - 1])
            && char.IsLowSurrogate(value[low]))
        {
            low--;
        }

        return low;
    }

    private static bool IsCollectedLog(string path)
    {
        var segments = Path.GetRelativePath(Path.GetPathRoot(path)!, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Contains("collected-logs", StringComparer.Ordinal);
    }

    private static void EnsureSafeEvidence(string contents, SensitiveDataRedactor redactor)
    {
        if (contents.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A private key was detected in host-test diagnostics; no failure artifacts were retained.");
        }

        if (redactor.ContainsKnownSecret(contents))
        {
            throw new InvalidOperationException("A generated secret survived host-test diagnostic redaction; no failure artifacts were retained.");
        }
    }

    private void ValidateCaseOwnership()
    {
        EnsureChildPath(_casesRoot, CaseDirectory, "case directory");
        var marker = Path.Combine(CaseDirectory, CaseMarkerFileName);
        if (!File.Exists(marker)
            || !string.Equals(File.ReadAllText(marker).Trim(), _caseToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to clean host-test case '{CaseDirectory}' because its ownership marker is missing or changed.");
        }
    }

    private static void EnsureChildPath(string parent, string child, string description)
    {
        var parentFullPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childFullPath = Path.GetFullPath(child);
        if (!childFullPath.StartsWith(parentFullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The generated {description} escaped its artifact root.");
        }
    }

    private static string SanitizeName(string value)
    {
        var safe = new string(value
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '_')
            .ToArray())
            .Trim('.', '-', '_');
        if (safe.Length == 0)
        {
            safe = "host-test";
        }

        return safe.Length > 64 ? safe[..64] : safe;
    }
}
