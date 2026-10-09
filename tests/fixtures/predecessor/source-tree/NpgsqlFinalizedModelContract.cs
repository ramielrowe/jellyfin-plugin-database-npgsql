using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Enforces the complete finalized EF model audited for this plugin release.
/// </summary>
/// <remarks>
/// The long metadata representation covers entities, properties, keys, indexes,
/// relationships, mappings, provider annotations, functions, and sequences. The
/// representation is normalized before hashing so the checked-in contract is
/// stable across operating-system line endings. Any host or provider model change
/// therefore fails while EF is building the model, before a connection or schema
/// operation can occur.
/// </remarks>
internal sealed class NpgsqlFinalizedModelContract : IModelFinalizedConvention
{
    /// <summary>The reviewed complete finalized-model SHA-256.</summary>
    internal const string ExpectedSha256 = "3A1CF5F43269EB4987B3E762B380BF8A2D39F639BB69049F3594AEC076257F5E";

    /// <inheritdoc />
    public IModel ProcessModelFinalized(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Validate(model);
        return model;
    }

    /// <summary>Calculates the deterministic complete model fingerprint.</summary>
    /// <param name="model">The finalized model.</param>
    /// <returns>An uppercase SHA-256 hex string.</returns>
    internal static string CalculateFingerprint(IModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var contractBuilder = new StringBuilder(
            model.ToDebugString(MetadataDebugStringOptions.LongDefault).ReplaceLineEndings("\n"));
        foreach (var function in model.GetDbFunctions().OrderBy(value => value.ModelName, StringComparer.Ordinal))
        {
            contractBuilder.Append("\nFUNCTION|")
                .Append(function.ModelName).Append('|')
                .Append(function.Schema).Append('|')
                .Append(function.Name).Append('|')
                .Append(function.ReturnType.AssemblyQualifiedName).Append('|')
                .Append(function.StoreType).Append('|')
                .Append(function.IsBuiltIn).Append('|')
                .Append(function.IsScalar).Append('|')
                .Append(function.IsAggregate).Append('|')
                .Append(function.IsNullable);
            foreach (var parameter in function.Parameters)
            {
                contractBuilder.Append("\nPARAMETER|")
                    .Append(parameter.Name).Append('|')
                    .Append(parameter.ClrType.AssemblyQualifiedName).Append('|')
                    .Append(parameter.StoreType).Append('|')
                    .Append(parameter.PropagatesNullability);
            }
        }

        foreach (var sequence in model.GetSequences().OrderBy(value => value.Name, StringComparer.Ordinal))
        {
            contractBuilder.Append("\nSEQUENCE|")
                .Append(sequence.Schema).Append('|')
                .Append(sequence.Name).Append('|')
                .Append(sequence.Type.AssemblyQualifiedName).Append('|')
                .Append(sequence.StartValue.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(sequence.IncrementBy.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(sequence.MinValue?.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(sequence.MaxValue?.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(sequence.IsCyclic);
        }

        var contract = contractBuilder.ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract)));
    }

    /// <summary>Rejects an unreviewed finalized model.</summary>
    /// <param name="model">The finalized model.</param>
    internal static void Validate(IModel model)
    {
        var actual = CalculateFingerprint(model);
        if (!string.Equals(actual, ExpectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The complete finalized Jellyfin database model has fingerprint {actual}, but "
                + $"{PluginMetadata.Name} {PluginMetadata.Version} was reviewed for {ExpectedSha256}. "
                + "Refusing to use the database before any schema operation. Regenerate and review the "
                + "golden contract only as part of an audited host-model compatibility update.");
        }
    }
}
