using System.Diagnostics;
using System.Text.Json;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>Creates and scavenges only marker-owned, owner-private plugin scratch directories.</summary>
#pragma warning disable SA1600 // Internal scratch lifecycle is documented at the type boundary.
internal static class NpgsqlScratchSpace
{
    /// <summary>Optional override for the private scratch root.</summary>
    internal const string RootEnvironmentVariable = "JELLYFIN_NPGSQL_SCRATCH_ROOT";

    private const string OwnerMarker = ".jellyfin-npgsql-owner.json";
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode OwnerFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string GetValidationRoot()
    {
        var configured = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.CurrentDirectory, ".artifacts", "npgsql-scratch")
            : configured;
        return Path.Combine(Path.GetFullPath(root), "validation");
    }

    internal static string CreateOwnedDirectory(string parent, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        Directory.CreateDirectory(parent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(parent, OwnerDirectoryMode);
        }

        var directory = Path.Combine(parent, purpose + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, OwnerDirectoryMode);
        }

        var marker = Path.Combine(directory, OwnerMarker);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerFileMode;
        }

        using (var stream = new FileStream(marker, options))
        {
            JsonSerializer.Serialize(
                stream,
                new ScratchOwner(
                    PluginMetadata.Id.ToString("D"),
                    Environment.ProcessId,
                    Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                    purpose));
            stream.Flush(flushToDisk: true);
        }

        NpgsqlBackupFileSystem.FlushDirectory(directory, "flush-scratch-owner");
        NpgsqlBackupFileSystem.FlushDirectory(parent, "flush-scratch-parent");
        return directory;
    }

    internal static void ScavengeValidationDirectories(string parent)
    {
        if (!Directory.Exists(parent))
        {
            return;
        }

        foreach (var candidate in Directory.EnumerateDirectories(parent, "validation-*", SearchOption.TopDirectoryOnly))
        {
            var marker = Path.Combine(candidate, OwnerMarker);
            ScratchOwner? owner;
            try
            {
                using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
                owner = JsonSerializer.Deserialize<ScratchOwner>(stream);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                continue;
            }

            if (owner is null
                || !string.Equals(owner.Provider, PluginMetadata.Id.ToString("D"), StringComparison.Ordinal)
                || !string.Equals(owner.Purpose, "validation", StringComparison.Ordinal)
                || IsLiveOwner(owner))
            {
                continue;
            }

            try
            {
                Directory.Delete(candidate, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A concurrent validator may have adopted or removed it. A later startup retries.
            }
        }
    }

    private static bool IsLiveOwner(ScratchOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return process.StartTime.ToUniversalTime() == owner.ProcessStartUtc;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed record ScratchOwner(
        string Provider,
        int ProcessId,
        DateTime ProcessStartUtc,
        string Purpose);
}
#pragma warning restore SA1600
