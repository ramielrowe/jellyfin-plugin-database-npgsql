using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Jellyfin.Plugin.Database.Npgsql;

/// <summary>
/// Applies provider model changes after Jellyfin has applied its shared configurations.
/// </summary>
public sealed class NpgsqlModelCustomizer : ModelCustomizer
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NpgsqlModelCustomizer"/> class.
    /// </summary>
    /// <param name="dependencies">The EF model customizer dependencies.</param>
    public NpgsqlModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        var baseItem = modelBuilder.Model.FindEntityType("Jellyfin.Database.Implementations.Entities.BaseItemEntity")
            ?? throw new InvalidOperationException("The Jellyfin BaseItemEntity model was not available to the late model hook.");
        var typeProperty = baseItem.FindProperty("Type")
            ?? throw new InvalidOperationException("The Jellyfin BaseItemEntity.Type property was not available to the late model hook.");

        typeProperty.SetMaxLength(511);
        modelBuilder.Model.SetAnnotation("JellyfinNpgsql:LateModelHook", true);
    }
}
