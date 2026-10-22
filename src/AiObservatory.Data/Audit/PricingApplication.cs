using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiObservatory.Data.Audit;

/// <summary>Immutable history. Revision zero on an event means no historical association is known.</summary>
public sealed class PricingApplication
{
    public Guid EventId { get; init; }
    public long Revision { get; init; }
    public string RevisionDigest { get; init; } = "";
    public string Kind { get; init; } = "";
    public Guid? SnapshotId { get; init; }
    public string? SnapshotHash { get; init; }
    public string? RateKey { get; init; }
    public byte[] Dimensions { get; init; } = [];
    public DateOnly PricingDate { get; init; }
    public string RuleVersion { get; init; } = "1";
    public decimal? CostUsd { get; init; }
    public decimal? CacheSavingsUsd { get; init; }
}

internal sealed class PricingApplicationConfiguration : IEntityTypeConfiguration<PricingApplication>
{
    public void Configure(EntityTypeBuilder<PricingApplication> b)
    {
        b.ToTable(
            "PricingApplications",
            t => t.HasCheckConstraint("CK_PricingApplication_Revision", "\"Revision\" > 0")
        );
        b.HasKey(x => new { x.EventId, x.Revision });
        b.Property(x => x.RevisionDigest).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        b.Property(x => x.SnapshotHash).HasMaxLength(64);
        b.Property(x => x.RateKey).HasMaxLength(64);
        b.Property(x => x.Dimensions).IsRequired();
        b.Property(x => x.RuleVersion).HasMaxLength(32).IsRequired();
        b.HasOne<UsageEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PricingSnapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Restrict);
    }
}
