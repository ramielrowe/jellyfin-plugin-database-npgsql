using System;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

public sealed record PostgreSqlTestOptions
{
    public const string AdministratorConnectionEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_ADMIN_CONNECTION_STRING";
    public const string UseContainerEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_USE_CONTAINER";
    public const string RequiredEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_REQUIRED";
    public const string WorkerEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_WORKER";
    public const string ContainerImageEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_POSTGRES_IMAGE";
    public const string DefaultContainerImage = "postgres@sha256:74935e72241653ca55e0414067e6d8763aceb8a810eb51b452253ec3dcfc4336";

    public string? AdministratorConnectionString { get; init; }

    public bool UseContainer { get; init; }

    public bool Required { get; init; }

    public string? WorkerId { get; init; }

    public string ContainerImage { get; init; } = DefaultContainerImage;

    public bool IsConfigured => UseContainer || !string.IsNullOrWhiteSpace(AdministratorConnectionString);

    public string MissingInfrastructureMessage =>
        $"Real PostgreSQL tests are opt-in. Set {AdministratorConnectionEnvironmentVariable} to an explicit test-only administrator connection, "
        + $"or set {UseContainerEnvironmentVariable}=true to use the digest-pinned PostgreSQL container. "
        + $"Set {RequiredEnvironmentVariable}=true in CI to turn missing infrastructure into a failure.";

    public static PostgreSqlTestOptions FromEnvironment()
    {
        var options = new PostgreSqlTestOptions
        {
            AdministratorConnectionString = Environment.GetEnvironmentVariable(AdministratorConnectionEnvironmentVariable),
            UseContainer = IsTrue(Environment.GetEnvironmentVariable(UseContainerEnvironmentVariable)),
            Required = IsTrue(Environment.GetEnvironmentVariable(RequiredEnvironmentVariable)),
            WorkerId = Environment.GetEnvironmentVariable(WorkerEnvironmentVariable),
            ContainerImage = Environment.GetEnvironmentVariable(ContainerImageEnvironmentVariable) ?? DefaultContainerImage
        };

        if (options.UseContainer && !string.IsNullOrWhiteSpace(options.AdministratorConnectionString))
        {
            throw new InvalidOperationException(
                $"Set only one of {AdministratorConnectionEnvironmentVariable} and {UseContainerEnvironmentVariable}; the fixture will never guess which server may be modified.");
        }

        if (options.UseContainer && !IsDigestPinnedImage(options.ContainerImage))
        {
            throw new InvalidOperationException(
                $"{ContainerImageEnvironmentVariable} must be an immutable image reference containing '@sha256:' followed by 64 hexadecimal characters.");
        }

        if (options.Required && !options.IsConfigured)
        {
            throw new InvalidOperationException(options.MissingInfrastructureMessage);
        }

        return options;
    }

    public static bool IsDigestPinnedImage(string image)
    {
        var marker = image.LastIndexOf("@sha256:", StringComparison.Ordinal);
        if (marker <= 0 || image.Length - marker - "@sha256:".Length != 64)
        {
            return false;
        }

        foreach (var character in image.AsSpan(marker + "@sha256:".Length))
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTrue(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
