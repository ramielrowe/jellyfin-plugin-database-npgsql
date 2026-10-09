using System.Reflection;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.UnitTests;

public sealed class EarlyLoadContractTests
{
    [Fact]
    public void ReleaseAssemblyExportsExactlyOneConcreteDatabaseProvider()
    {
        var providerTypes = typeof(PluginMetadata).Assembly
            .GetExportedTypes()
            .Where(type => !type.IsAbstract && typeof(IJellyfinDatabaseProvider).IsAssignableFrom(type))
            .ToArray();

        var providerType = Assert.Single(providerTypes);
        Assert.Equal(typeof(NpgsqlDatabaseProvider), providerType);
    }

    [Fact]
    public void ProviderConstructorsUseOnlyEarlyHostServices()
    {
        var constructors = typeof(NpgsqlDatabaseProvider).GetConstructors();
        Assert.Equal(2, constructors.Length);
        Assert.Contains(
            constructors,
            constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
                [typeof(ILogger<NpgsqlDatabaseProvider>)]));
        Assert.Contains(
            constructors,
            constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
                [typeof(IServiceProvider), typeof(ILogger<NpgsqlDatabaseProvider>)]));
    }

    [Fact]
    public void EarlyProviderTypeSignaturesDoNotRequireNpgsqlAssemblies()
    {
        var providerType = typeof(NpgsqlDatabaseProvider);
        var signatureTypes = providerType
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(field => field.FieldType)
            .Concat(providerType
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .Select(property => property.PropertyType))
            .Concat(providerType
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType)))
            .SelectMany(FlattenType)
            .Select(type => type.Assembly.GetName().Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.DoesNotContain(signatureTypes, name => name!.StartsWith("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void NpgsqlEfBootstrapCannotBeInlinedIntoEarlyValidation()
    {
        var configure = typeof(NpgsqlOptionsBootstrap).GetMethod(
            "Configure",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(configure);
        Assert.True((configure.MethodImplementationFlags & MethodImplAttributes.NoInlining) != 0);
    }

    [Theory]
    [InlineData(12, 2, 0, 0, true)]
    [InlineData(12, 2, 99, 0, false)]
    [InlineData(12, 1, 0, 0, false)]
    [InlineData(12, 3, 0, 0, false)]
    [InlineData(13, 0, 0, 0, false)]
    public void SupportedHostGuardMatchesTheReleaseAbi(
        int major,
        int minor,
        int build,
        int revision,
        bool expected)
    {
        Assert.Equal(expected, NpgsqlDatabaseProvider.SupportsHostDatabaseAbi(new Version(major, minor, build, revision)));
    }

    [Fact]
    public void PrefixSelectedDirectoryIsRejectedBeforeConfigurationOrDatabaseAccess()
    {
        var provider = new NpgsqlDatabaseProvider(NullLogger<NpgsqlDatabaseProvider>.Instance);
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = "PLUGIN_PROVIDER",
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = PluginMetadata.InstallationDirectory + "-ambiguous",
                PluginAssembly = PluginMetadata.AssemblyFileName,
                ConnectionString = "Host=must-not-be-resolved.invalid;Database=must_not_connect"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.Initialise(new DbContextOptionsBuilder(), configuration));

        Assert.Contains("exact folder and PluginName", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No database connection was attempted", exception.Message, StringComparison.Ordinal);
        Assert.Null(provider.DbContextFactory);
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var nestedType in FlattenType(elementType))
            {
                yield return nestedType;
            }
        }

        foreach (var genericArgument in type.GetGenericArguments())
        {
            foreach (var nestedType in FlattenType(genericArgument))
            {
                yield return nestedType;
            }
        }
    }
}
