using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Applies Npgsql model changes after Jellyfin has applied its shared configurations.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="NpgsqlModelCustomizer"/> class.
/// </remarks>
/// <param name="dependencies">The EF model customizer dependencies.</param>
public sealed class NpgsqlModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(context);
        base.Customize(modelBuilder, context);

        var baseItem = modelBuilder.Model.FindEntityType("Jellyfin.Database.Implementations.Entities.BaseItemEntity")
            ?? throw new InvalidOperationException("The Jellyfin BaseItemEntity model was not available to the late model hook.");
        var typeProperty = baseItem.FindProperty("Type")
            ?? throw new InvalidOperationException("The Jellyfin BaseItemEntity.Type property was not available to the late model hook.");

        typeProperty.SetMaxLength(511);
        modelBuilder.Model.SetAnnotation("JellyfinNpgsql:LateModelHook", true);
    }
}
