using FCG.Catalog.Domain.Entities;
using FCG.Catalog.Infrastructure.Persistence.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FCG.Catalog.Infrastructure.Persistence.Configurations;

public sealed class SearchIndexPendingConfiguration : IEntityTypeConfiguration<SearchIndexPending>
{
    public void Configure(EntityTypeBuilder<SearchIndexPending> builder)
    {
        builder.ToTable("SearchIndexPending");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.PayloadJson).IsRequired();
        builder.Property(item => item.LastErrorCode).HasMaxLength(100);
        builder.HasIndex(item => new { item.CompletedAtUtc, item.NextAttemptAtUtc, item.CreatedAtUtc, item.Id });
        builder.HasOne<Game>().WithMany().HasForeignKey(item => item.GameId).OnDelete(DeleteBehavior.Restrict);
    }
}
