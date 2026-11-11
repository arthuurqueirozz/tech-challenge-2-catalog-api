using FCG.Catalog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FCG.Catalog.Infrastructure.Persistence.Configurations;

public sealed class GameConfiguration : IEntityTypeConfiguration<Game>
{
    public void Configure(EntityTypeBuilder<Game> builder)
    {
        builder.ToTable("Games");
        builder.HasKey(game => game.Id);
        builder.Property(game => game.Title)
            .HasMaxLength(Game.MaxTitleLength)
            .IsRequired();
        builder.Property(game => game.Description)
            .HasMaxLength(Game.MaxDescriptionLength);
        builder.Property(game => game.Developer)
            .HasMaxLength(Game.MaxDeveloperLength);
        builder.Property(game => game.Price)
            .HasPrecision(18, 2);
        builder.Property(game => game.SearchRevision).HasDefaultValue(1L).IsConcurrencyToken();
        builder.HasIndex(game => game.Title);
    }
}
