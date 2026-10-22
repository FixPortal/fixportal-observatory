using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NodaTime;

namespace AiObservatory.Data.Audit;

internal sealed class AuditExportReadLease
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string InstanceId { get; set; } = "";
    public Instant ExpiresAt { get; set; }
    public bool Cancelled { get; set; }
    public long ReservedBytes { get; set; }
}

internal sealed class AuditExportReadLeaseConfiguration : IEntityTypeConfiguration<AuditExportReadLease>
{
    public void Configure(EntityTypeBuilder<AuditExportReadLease> b)
    {
        b.ToTable("AuditExportReadLeases");
        b.HasKey(x => x.Id);
        b.Property(x => x.InstanceId).HasMaxLength(128);
        b.HasIndex(x => new { x.InstanceId, x.ExpiresAt });
        b.HasIndex(x => x.RunId);
        b.HasOne<AuditRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
