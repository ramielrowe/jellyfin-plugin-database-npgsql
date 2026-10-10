using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

if (args.Length == 3 && args[0] == "verify-symbols")
{
    try
    {
        VerifyPortableSymbols(args[1], args[2]);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Jellyfin.Plugin.Database.Npgsql.Packaging <source-directory> <archive-path>");
    Console.Error.WriteLine("   or: Jellyfin.Plugin.Database.Npgsql.Packaging verify-symbols <provider-dll> <portable-pdb>");
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

static void VerifyPortableSymbols(string assemblyPath, string pdbPath)
{
    using var assemblyStream = File.OpenRead(Path.GetFullPath(assemblyPath));
    using var peReader = new PEReader(assemblyStream);
    if (!peReader.HasMetadata)
    {
        throw new InvalidOperationException("The provider artifact is not a managed PE assembly.");
    }

    var codeViewEntries = peReader
        .ReadDebugDirectory()
        .Where(entry => entry.Type == DebugDirectoryEntryType.CodeView)
        .ToArray();
    if (codeViewEntries.Length != 1)
    {
        throw new InvalidOperationException("The provider assembly must contain exactly one CodeView debug entry.");
    }

    var codeViewEntry = codeViewEntries[0];
    var codeView = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);
    if (codeView.Age != 1)
    {
        throw new InvalidOperationException("The provider CodeView entry does not describe portable PDB symbols.");
    }

    using var pdbStream = File.OpenRead(Path.GetFullPath(pdbPath));
    using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
    var pdbReader = pdbProvider.GetMetadataReader();
    var debugMetadataHeader = pdbReader.DebugMetadataHeader
        ?? throw new InvalidOperationException("The symbol artifact has no portable PDB debug metadata header.");
    var pdbId = new BlobContentId(debugMetadataHeader.Id);
    if (codeView.Guid != pdbId.Guid || codeViewEntry.Stamp != pdbId.Stamp)
    {
        throw new InvalidOperationException(
            "The portable PDB debug identifier does not match the provider assembly CodeView identifier.");
    }

    if (!string.Equals(
            Path.GetFileName(codeView.Path),
            Path.GetFileName(pdbPath),
            StringComparison.Ordinal))
    {
        throw new InvalidOperationException("The provider CodeView entry names a different symbol artifact.");
    }

    Console.WriteLine($"Portable symbols match provider debug ID {pdbId.Guid:N}/{pdbId.Stamp:x8}.");
}
