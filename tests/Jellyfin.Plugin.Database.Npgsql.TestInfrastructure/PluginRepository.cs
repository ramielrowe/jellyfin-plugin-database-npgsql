using System;
using System.IO;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public static class PluginRepository
{
    public const string SolutionFileName = "Jellyfin.Plugin.Database.Npgsql.slnx";

    public static string FindRoot(string startPath)
    {
        var current = File.Exists(startPath)
            ? new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(startPath))!)
            : new DirectoryInfo(Path.GetFullPath(startPath));
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, SolutionFileName)))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate {SolutionFileName}. Run from the standalone plugin repository or provide paths explicitly.");
    }
}
