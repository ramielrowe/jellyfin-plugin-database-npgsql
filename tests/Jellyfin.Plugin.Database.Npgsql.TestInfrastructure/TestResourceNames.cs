using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

/// <summary>
/// Creates and validates names which are safe for destructive test cleanup.
/// </summary>
public static partial class TestResourceNames
{
    public const string DatabasePrefix = "jellyfin_test_db_";
    public const string RolePrefix = "jellyfin_test_role_";
    public const string ContainerPrefix = "jellyfin-npgsql-test-container-";
    public const string AdministratorDatabaseMarker = "jellyfin_npgsql_test_admin";

    public static TestResourceIdentity Create(string? workerId = null)
    {
        var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var worker = NormalizeWorkerId(workerId);
        var taggedSuffix = string.IsNullOrEmpty(worker) ? suffix : worker + "_" + suffix;
        return new TestResourceIdentity(
            DatabasePrefix + taggedSuffix,
            RolePrefix + taggedSuffix,
            ContainerPrefix + taggedSuffix.Replace('_', '-'));
    }

    public static bool IsOwnedDatabase(string name) => DatabaseNameExpression().IsMatch(name);

    public static bool IsOwnedRole(string name) => RoleNameExpression().IsMatch(name);

    public static bool IsOwnedContainer(string name) => ContainerNameExpression().IsMatch(name);

    public static void EnsureOwnedDatabase(string name)
    {
        if (!IsOwnedDatabase(name))
        {
            throw new InvalidOperationException("Refusing to alter a database not created by the Npgsql test fixture.");
        }
    }

    public static void EnsureOwnedRole(string name)
    {
        if (!IsOwnedRole(name))
        {
            throw new InvalidOperationException("Refusing to alter a role not created by the Npgsql test fixture.");
        }
    }

    public static void EnsureOwnedContainer(string name)
    {
        if (!IsOwnedContainer(name))
        {
            throw new InvalidOperationException("Refusing to remove a container not created by the Npgsql test fixture.");
        }
    }

    private static string NormalizeWorkerId(string? workerId)
    {
        if (string.IsNullOrWhiteSpace(workerId))
        {
            return string.Empty;
        }

        var normalized = NonIdentifierCharacterExpression().Replace(workerId.Trim().ToLowerInvariant(), "_").Trim('_');
        return normalized.Length > 10 ? normalized[..10] : normalized;
    }

    [GeneratedRegex("^jellyfin_test_db_(?:[a-z0-9_]{1,11}_)?[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNameExpression();

    [GeneratedRegex("^jellyfin_test_role_(?:[a-z0-9_]{1,11}_)?[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNameExpression();

    [GeneratedRegex("^jellyfin-npgsql-test-container-(?:[a-z0-9-]{1,17}-)?[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerNameExpression();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonIdentifierCharacterExpression();
}

public sealed record TestResourceIdentity(string DatabaseName, string RoleName, string ContainerName);
