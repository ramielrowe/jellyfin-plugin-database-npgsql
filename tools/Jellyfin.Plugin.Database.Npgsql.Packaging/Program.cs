using System.IO.Compression;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Jellyfin.Plugin.Database.Npgsql.Packaging <source-directory> <archive-path>");
    return 2;
}

try
{
    var sourceDirectory = Path.GetFullPath(args[0]);
    var archivePath = Path.GetFullPath(args[1]);
    if (!Directory.Exists(sourceDirectory))
    {
        throw new InvalidOperationException("The package source directory does not exist.");
    }

    var entries = Directory
        .EnumerateFileSystemEntries(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
        .Select(path => new FileInfo(path))
        .OrderBy(file => file.Name, StringComparer.Ordinal)
        .ToArray();
    if (entries.Length == 0)
    {
        throw new InvalidOperationException("The package source directory is empty.");
    }

    foreach (var entry in entries)
    {
        if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Package entry must be a regular file: {entry.Name}");
        }
    }

    Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
    using var destination = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
    using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
    foreach (var file in entries)
    {
        var entry = archive.CreateEntry(file.Name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = Convert.ToInt32("100644", 8) << 16;
        using var input = file.OpenRead();
        using var output = entry.Open();
        input.CopyTo(output);
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
