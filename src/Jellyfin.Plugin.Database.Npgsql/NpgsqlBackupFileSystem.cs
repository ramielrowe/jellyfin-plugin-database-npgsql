using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>Provides the fail-closed durable filesystem operations used to publish snapshots.</summary>
internal static class NpgsqlBackupFileSystem
{
    private const int OpenReadOnly = 0;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private static readonly AsyncLocal<Action<string>?> FaultCallback = new();

    /// <summary>Installs a per-async-flow fault callback for integration tests.</summary>
    /// <param name="callback">A callback which may throw at a named durability point.</param>
    /// <returns>A scope which restores the previous callback.</returns>
    internal static IDisposable InjectFaults(Action<string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var previous = FaultCallback.Value;
        FaultCallback.Value = callback;
        return new FaultScope(previous);
    }

    /// <summary>Reaches a deterministic testable filesystem boundary without changing state.</summary>
    /// <param name="point">The boundary name.</param>
    internal static void Checkpoint(string point) => FaultCallback.Value?.Invoke(point);

    /// <summary>Forces a completed file write to stable storage.</summary>
    /// <param name="stream">The writable file stream.</param>
    /// <param name="point">The diagnostic fault point.</param>
    internal static void FlushFile(FileStream stream, string point)
    {
        FaultCallback.Value?.Invoke(point);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Forces directory metadata to stable storage.</summary>
    /// <param name="path">The directory to flush.</param>
    /// <param name="point">The diagnostic fault point.</param>
    internal static void FlushDirectory(string path, string point)
    {
        FaultCallback.Value?.Invoke(point);
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFile(
                path,
                GenericWrite,
                ShareRead | ShareWrite | ShareDelete,
                0,
                OpenExisting,
                BackupSemantics,
                0);
            if (handle.IsInvalid)
            {
                throw new IOException("The PostgreSQL migration backup directory could not be opened for durable publication.", new Win32Exception());
            }

            if (!FlushFileBuffers(handle))
            {
                throw new IOException("The PostgreSQL migration backup directory could not be flushed for durable publication.", new Win32Exception());
            }

            return;
        }

        var descriptor = Open(path, OpenReadOnly);
        if (descriptor < 0)
        {
            throw new IOException("The PostgreSQL migration backup directory could not be opened for durable publication.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException("The PostgreSQL migration backup directory could not be flushed for durable publication.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    /// <summary>Atomically publishes and durably records a completed snapshot directory.</summary>
    /// <param name="partialDirectory">The private partial directory.</param>
    /// <param name="finalDirectory">The completed directory name.</param>
    /// <param name="root">The parent backup root.</param>
    internal static void PublishDirectory(string partialDirectory, string finalDirectory, string root)
    {
        FlushDirectory(root, "flush-root-before-rename");
        FaultCallback.Value?.Invoke("before-rename");
        Directory.Move(partialDirectory, finalDirectory);
        FaultCallback.Value?.Invoke("after-rename");
        FlushDirectory(root, "flush-root-after-rename");
    }

#pragma warning disable CA2101 // The Unix path is explicitly marshalled as UTF-8 rather than the platform-dependent ANSI default.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
#pragma warning restore CA2101

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Close(int descriptor);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        [MarshalAs(UnmanagedType.LPWStr)] string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle fileHandle);

    private sealed class FaultScope(Action<string>? previous) : IDisposable
    {
        public void Dispose() => FaultCallback.Value = previous;
    }
}
