using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed class JellyfinHostLayout
{
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
        var runName = "jellyfin-npgsql-test-run-" + (workerId ?? "worker") + "-" + Guid.NewGuid().ToString("N");
        var root = Path.Combine(Path.GetFullPath(artifactRoot), runName);
        var layout = new JellyfinHostLayout(root, publish);
        Directory.CreateDirectory(layout.ConfigurationDirectory);
        Directory.CreateDirectory(layout.CacheDirectory);
        Directory.CreateDirectory(layout.DataDirectory);
        Directory.CreateDirectory(layout.MediaDirectory);
        Directory.CreateDirectory(layout.LogDirectory);
        Directory.CreateDirectory(layout.CollectedLogDirectory);
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
        Directory.CreateDirectory(pluginRoot);
        var destination = Path.Combine(pluginRoot, pluginFolderName);
        Directory.CreateDirectory(destination);
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
        document.Save(Path.Combine(ConfigurationDirectory, "database.xml"));
    }

    public void WriteNetworkConfiguration(int httpPort, int httpsPort)
    {
        if (httpPort is < 1024 or > 65535 || httpsPort is < 1024 or > 65535 || httpPort == httpsPort)
        {
            throw new ArgumentOutOfRangeException(nameof(httpPort), "Test HTTP and HTTPS ports must be distinct non-privileged ports.");
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
        document.Save(Path.Combine(ConfigurationDirectory, "network.xml"));
    }

    public void CollectSanitizedLogs(SensitiveDataRedactor redactor)
    {
        Directory.CreateDirectory(CollectedLogDirectory);
        foreach (var source in Directory.EnumerateFiles(LogDirectory, "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(RunRoot, "process*.log", SearchOption.TopDirectoryOnly)))
        {
            var relative = Path.GetRelativePath(RunRoot, source).Replace(Path.DirectorySeparatorChar, '_');
            File.WriteAllText(
                Path.Combine(CollectedLogDirectory, relative),
                redactor.Redact(File.ReadAllText(source)));
        }
    }
}
