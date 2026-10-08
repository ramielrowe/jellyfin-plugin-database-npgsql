using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class JellyfinHostLayout
{
    private const int MaximumEvidenceFileBytes = 4 * 1024 * 1024;
    private const int MaximumEvidenceBytes = 32 * 1024 * 1024;
    private JellyfinHostLayout(string runRoot, PristineJellyfinPublish publish)
    {
        RunRoot = runRoot;
        Publish = publish;
        ConfigurationDirectory = Path.Combine(runRoot, "config");
        CacheDirectory = Path.Combine(runRoot, "cache");
        DataDirectory = Path.Combine(runRoot, "data");
        MediaDirectory = Path.Combine(runRoot, "media");
        LogDirectory = Path.Combine(runRoot, "logs");
        CollectedLogDirectory = Path.Combine(runRoot, "collected-logs");
    }

    public string RunRoot { get; }

    public PristineJellyfinPublish Publish { get; }

    public string ConfigurationDirectory { get; }

    public string CacheDirectory { get; }

    public string DataDirectory { get; }

    public string MediaDirectory { get; }

    public string LogDirectory { get; }

    public string CollectedLogDirectory { get; }

    public static JellyfinHostLayout Create(string artifactRoot, PristineJellyfinPublish publish, string? workerId = null)
    {
        var safeWorker = new string((workerId ?? "worker").Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());
        safeWorker = safeWorker.Trim('_');
        if (safeWorker.Length == 0)
        {
            safeWorker = "worker";
        }

        safeWorker = safeWorker.Length > 32 ? safeWorker[..32] : safeWorker;
        var runName = "jellyfin-npgsql-test-run-" + safeWorker + "-" + Guid.NewGuid().ToString("N");
        var artifactRootFullPath = Path.GetFullPath(artifactRoot);
        var root = Path.GetFullPath(Path.Combine(artifactRootFullPath, runName));
        if (!root.StartsWith(artifactRootFullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The generated test run path escaped its artifact root.");
        }
        var layout = new JellyfinHostLayout(root, publish);
        SecureFileSystem.CreateDirectory(layout.RunRoot);
        SecureFileSystem.CreateDirectory(layout.ConfigurationDirectory);
        SecureFileSystem.CreateDirectory(layout.CacheDirectory);
        SecureFileSystem.CreateDirectory(layout.DataDirectory);
        SecureFileSystem.CreateDirectory(layout.MediaDirectory);
        SecureFileSystem.CreateDirectory(layout.LogDirectory);
        SecureFileSystem.CreateDirectory(layout.CollectedLogDirectory);
        return layout;
    }

    public string InstallPluginZip(string packageZip, string pluginFolderName)
    {
        if (string.IsNullOrWhiteSpace(pluginFolderName)
            || pluginFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || pluginFolderName is "." or "..")
        {
            throw new InvalidOperationException("The test plugin folder name is invalid.");
        }

        var pluginRoot = Path.Combine(DataDirectory, "plugins");
        SecureFileSystem.CreateDirectory(pluginRoot);
        var destination = Path.Combine(pluginRoot, pluginFolderName);
        SecureFileSystem.CreateDirectory(destination);
        PristineJellyfinPublish.ExtractZipSafely(packageZip, destination);
        return destination;
    }

    public void WriteDatabaseConfiguration(string connectionString, string pluginFolderName, string pluginAssembly)
    {
        var document = new XDocument(
            new XElement(
                "DatabaseConfigurationOptions",
                new XElement("DatabaseType", "PLUGIN_PROVIDER"),
                new XElement(
                    "CustomProviderOptions",
                    new XElement("PluginName", pluginFolderName),
                    new XElement("PluginAssembly", pluginAssembly),
                    new XElement("ConnectionString", connectionString),
                    new XElement("Options")),
                new XElement("LockingBehavior", "NoLock")));
        SecureFileSystem.WriteAllTextAtomic(Path.Combine(ConfigurationDirectory, "database.xml"), document.ToString());
    }

    public void WriteNetworkConfiguration(int httpPort, int httpsPort)
    {
        if (httpPort is < 1 or > 65535 || httpsPort is < 1 or > 65535 || httpPort == httpsPort)
        {
            throw new ArgumentOutOfRangeException(nameof(httpPort), "Test HTTP and HTTPS ports must be distinct decimal values from 1 through 65535.");
        }

        var document = new XDocument(
            new XElement(
                "NetworkConfiguration",
                new XElement("InternalHttpPort", httpPort),
                new XElement("InternalHttpsPort", httpsPort),
                new XElement("PublicHttpPort", httpPort),
                new XElement("PublicHttpsPort", httpsPort),
                new XElement("AutoDiscovery", false),
                new XElement("EnableIPv4", true),
                new XElement("EnableIPv6", false),
                new XElement("EnableRemoteAccess", false),
                new XElement("LocalNetworkAddresses", new XElement("string", "127.0.0.1"))));
        SecureFileSystem.WriteAllTextAtomic(Path.Combine(ConfigurationDirectory, "network.xml"), document.ToString());
    }

    public void CollectSanitizedLogs(SensitiveDataRedactor redactor)
    {
        SecureFileSystem.CreateDirectory(CollectedLogDirectory);
        var retainedBytes = 0;
        foreach (var source in Directory.EnumerateFiles(LogDirectory, "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(RunRoot, "process*.log", SearchOption.TopDirectoryOnly)))
        {
            var relative = Path.GetRelativePath(RunRoot, source);
            var sourceBytes = File.ReadAllBytes(source);
            if (sourceBytes.Length > MaximumEvidenceFileBytes)
            {
                Array.Resize(ref sourceBytes, MaximumEvidenceFileBytes);
            }

            var sourceText = Encoding.UTF8.GetString(sourceBytes);
            if (sourceText.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
            {
                DeleteCollectedEvidence();
                throw new InvalidOperationException("A private key was detected in test output; no failure artifacts were retained.");
            }

            var sanitized = redactor.Redact(sourceText);
            if (redactor.ContainsKnownSecret(sanitized))
            {
                DeleteCollectedEvidence();
                throw new InvalidOperationException("A generated secret survived redaction; no failure artifacts were retained.");
            }

            var bytes = Encoding.UTF8.GetByteCount(sanitized);
            if (retainedBytes + bytes > MaximumEvidenceBytes)
            {
                break;
            }

            retainedBytes += bytes;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative))).ToLowerInvariant()[..16];
            var safeName = new string(Path.GetFileName(source).Select(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' ? character : '_').ToArray());
            SecureFileSystem.WriteAllTextAtomic(Path.Combine(CollectedLogDirectory, hash + "-" + safeName), sanitized);
        }
    }

    public void RetainOnlySanitizedEvidence(SensitiveDataRedactor redactor)
    {
        Exception? collectionError = null;
        try
        {
            CollectSanitizedLogs(redactor);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            collectionError = ex;
        }
        finally
        {
            foreach (var directory in new[] { ConfigurationDirectory, CacheDirectory, DataDirectory, MediaDirectory, LogDirectory })
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            foreach (var processLog in Directory.EnumerateFiles(RunRoot, "process*.log", SearchOption.TopDirectoryOnly))
            {
                File.Delete(processLog);
            }
        }

        if (collectionError is not null)
        {
            throw new InvalidOperationException("Raw test artifacts were removed, but sanitized log collection failed.", collectionError);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(CollectedLogDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        foreach (var evidence in Directory.EnumerateFiles(CollectedLogDirectory, "*", SearchOption.AllDirectories))
        {
            var contents = File.ReadAllText(evidence);
            if (redactor.ContainsKnownSecret(contents))
            {
                File.Delete(evidence);
                DeleteCollectedEvidence();
                throw new InvalidOperationException("Sanitized test evidence still contains a generated secret.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(evidence, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private void DeleteCollectedEvidence()
    {
        if (Directory.Exists(CollectedLogDirectory))
        {
            Directory.Delete(CollectedLogDirectory, recursive: true);
        }
    }
}
