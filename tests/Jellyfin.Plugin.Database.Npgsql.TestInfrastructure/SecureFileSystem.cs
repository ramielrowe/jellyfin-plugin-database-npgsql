using System;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

internal static class SecureFileSystem
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = Directory.CreateDirectory(path);
            return;
        }

        _ = Directory.CreateDirectory(path, DirectoryMode);
        File.SetUnixFileMode(path, DirectoryMode);
    }

    public static FileStream CreateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.ReadWrite,
            Mode = System.IO.FileMode.CreateNew,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        return new FileStream(path, options);
    }

    public static FileStream OpenLease(string path)
    {
        CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions
        {
            Access = FileAccess.ReadWrite,
            Mode = System.IO.FileMode.OpenOrCreate,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }

        var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, FileMode);
        }

        return stream;
    }

    public static void WriteAllTextAtomic(string path, string contents)
    {
        CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = CreateFile(temporary))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, FileMode);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
