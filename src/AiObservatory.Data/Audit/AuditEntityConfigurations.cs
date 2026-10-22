using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AiObservatory.Data.Audit;

internal sealed class AuditMachineConfiguration : IEntityTypeConfiguration<AuditMachine>
{
    public void Configure(EntityTypeBuilder<AuditMachine> b)
    {
        b.ToTable("AuditMachines");
        b.HasKey(x => x.Id);
    }
}

internal sealed class AuditMachineCredentialConfiguration : IEntityTypeConfiguration<AuditMachineCredential>
{
    public void Configure(EntityTypeBuilder<AuditMachineCredential> b)
    {
        b.ToTable(
            "AuditMachineCredentials",
            t => t.HasCheckConstraint("CK_AuditCredential_Hash", "octet_length(\"SecretHash\") = 32")
        );
        b.HasKey(x => x.Id);
        b.HasOne<AuditMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.MachineId);
    }
}

internal sealed class AuditCaptureRequestConfiguration : IEntityTypeConfiguration<AuditCaptureRequest>
{
    public void Configure(EntityTypeBuilder<AuditCaptureRequest> b)
    {
        b.ToTable(
            "AuditCaptureRequests",
            t =>
            {
                t.HasCheckConstraint(
                    "CK_AuditRequest_State",
                    "\"State\" IN ('pending','uploading','sealed','expired')"
                );
                t.HasCheckConstraint(
                    "CK_AuditRequest_Counts",
                    "\"UploadedBytes\" >= 0 AND \"UploadedBytes\" <= 536870912 AND \"RecordCount\" >= 0 AND \"RecordCount\" <= 1000000 AND \"Revision\" >= 0"
                );
            }
        );
        b.HasKey(x => x.Id);
        b.Property(x => x.State).HasMaxLength(20);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<AuditMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AuditMachineCredential>()
            .WithMany()
            .HasForeignKey(x => x.CredentialId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new
        {
            x.MachineId,
            x.State,
            x.Deadline,
        });
    }
}

internal sealed class AuditEvidenceCaptureConfiguration : IEntityTypeConfiguration<AuditEvidenceCapture>
{
    public void Configure(EntityTypeBuilder<AuditEvidenceCapture> b)
    {
        b.ToTable("AuditEvidenceCaptures");
        b.HasKey(x => x.Id);
        b.Property(x => x.Generation).HasMaxLength(256);
        b.Property(x => x.ManifestHash).HasMaxLength(64);
        b.Property(x => x.SealKey).HasMaxLength(128);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasOne<AuditCaptureRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AuditMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestId).IsUnique();
        b.HasIndex(x => new { x.RequestId, x.SealKey }).IsUnique();
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class AuditEvidenceChunkConfiguration : IEntityTypeConfiguration<AuditEvidenceChunk>
{
    public void Configure(EntityTypeBuilder<AuditEvidenceChunk> b)
    {
        b.ToTable(
            "AuditEvidenceChunks",
            t =>
            {
                t.HasCheckConstraint(
                    "CK_AuditChunk_Bounds",
                    "\"Index\" >= 0 AND \"Index\" < 10000 AND octet_length(\"Bytes\") > 0 AND octet_length(\"Bytes\") <= 8388608 AND \"RecordCount\" > 0 AND \"RecordCount\" <= 1000000"
                );
                t.HasCheckConstraint("CK_AuditChunk_Hash", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
            }
        );
        b.HasKey(x => new { x.RequestId, x.Index });
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.HasOne<AuditCaptureRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new
            {
                x.RequestId,
                x.Index,
                x.Sha256,
            })
            .IsUnique();
    }
}

internal sealed class AuditRunConfiguration : IEntityTypeConfiguration<AuditRun>
{
    public void Configure(EntityTypeBuilder<AuditRun> b)
    {
        b.ToTable(
            "AuditRuns",
            t =>
            {
                t.HasCheckConstraint(
                    "CK_AuditRun_State",
                    "\"State\" IN ('collecting','queued','executing','completed','cancelled','failed')"
                );
                t.HasCheckConstraint("CK_AuditRun_Attempts", "\"Attempts\" BETWEEN 0 AND 3 AND \"Revision\" >= 0");
            }
        );
        b.Property(x => x.Stage).HasMaxLength(32).HasDefaultValue("queued");
        b.Property(x => x.CaptureRequestIds).HasDefaultValueSql("'{}'::uuid[]");
        b.HasKey(x => x.Id);
        b.Property(x => x.InstanceId).HasMaxLength(128);
        b.Property(x => x.State).HasMaxLength(20);
        b.Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.ResultHash).HasMaxLength(64);
        b.Property(x => x.DiagnosticCode).HasMaxLength(128);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.InstanceId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new
        {
            x.InstanceId,
            x.State,
            x.CreatedAt,
            x.Id,
        });
        b.HasIndex(x => x.SummaryExpiresAt);
        b.HasOne<AuditRun>().WithMany().HasForeignKey(x => x.PredecessorRunId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<AuditRun>().WithMany().HasForeignKey(x => x.EvaluationOfRunId).OnDelete(DeleteBehavior.SetNull);
    }
}

internal sealed class AuditRunCaptureConfiguration : IEntityTypeConfiguration<AuditRunCapture>
{
    public void Configure(EntityTypeBuilder<AuditRunCapture> b)
    {
        b.ToTable("AuditRunCaptures");
        b.HasKey(x => new { x.RunId, x.CaptureId });
        b.HasOne<AuditRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<AuditEvidenceCapture>().WithMany().HasForeignKey(x => x.CaptureId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AuditServerSnapshotConfiguration : IEntityTypeConfiguration<AuditServerSnapshot>
{
    public void Configure(EntityTypeBuilder<AuditServerSnapshot> b)
    {
        b.ToTable(
            "AuditServerSnapshots",
            t =>
                t.HasCheckConstraint(
                    "CK_AuditSnapshot_Bounds",
                    "octet_length(\"Bytes\") <= 536870912 AND \"Revision\" >= 0"
                )
        );
        b.HasKey(x => x.Id);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => x.RunId).IsUnique();
        b.HasOne<AuditRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditFindingRecordConfiguration : IEntityTypeConfiguration<AuditFindingRecord>
{
    public void Configure(EntityTypeBuilder<AuditFindingRecord> b)
    {
        b.ToTable("AuditFindingRecords");
        b.HasKey(x => new { x.RunId, x.Index });
        b.HasOne<AuditRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditRetentionSettingsConfiguration : IEntityTypeConfiguration<AuditRetentionSettings>
{
    public void Configure(EntityTypeBuilder<AuditRetentionSettings> b)
    {
        b.ToTable(
            "AuditRetentionSettings",
            t =>
                t.HasCheckConstraint(
                    "CK_AuditRetention_Days",
                    "(\"EvidenceDays\" IS NULL OR \"EvidenceDays\" > 0) AND (\"SummaryDays\" IS NULL OR \"SummaryDays\" > 0) AND \"Revision\" >= 0"
                )
        );
        b.HasKey(x => x.InstanceId);
        b.Property(x => x.InstanceId).HasMaxLength(128);
        b.Property(x => x.Revision).IsConcurrencyToken();
    }
}
