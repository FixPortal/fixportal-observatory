using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditMachines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditMachines", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditRetentionSettings",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EvidenceDays = table.Column<int>(type: "integer", nullable: true),
                    SummaryDays = table.Column<int>(type: "integer", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRetentionSettings", x => x.InstanceId);
                    table.CheckConstraint(
                        "CK_AuditRetention_Days",
                        "(\"EvidenceDays\" IS NULL OR \"EvidenceDays\" > 0) AND (\"SummaryDays\" IS NULL OR \"SummaryDays\" > 0) AND \"Revision\" >= 0"
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Scope = table.Column<byte[]>(type: "bytea", nullable: false),
                    IdempotencyKey = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: false
                    ),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    EvidenceExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    SummaryExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<byte[]>(type: "bytea", nullable: true),
                    ResultHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DiagnosticCode = table.Column<string>(
                        type: "character varying(128)",
                        maxLength: 128,
                        nullable: true
                    ),
                    PredecessorRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvaluationOfRunId = table.Column<Guid>(type: "uuid", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRuns", x => x.Id);
                    table.CheckConstraint("CK_AuditRun_Attempts", "\"Attempts\" BETWEEN 0 AND 3 AND \"Revision\" >= 0");
                    table.CheckConstraint(
                        "CK_AuditRun_State",
                        "\"State\" IN ('collecting','queued','executing','completed','cancelled','failed')"
                    );
                    table.ForeignKey(
                        name: "FK_AuditRuns_AuditRuns_EvaluationOfRunId",
                        column: x => x.EvaluationOfRunId,
                        principalTable: "AuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull
                    );
                    table.ForeignKey(
                        name: "FK_AuditRuns_AuditRuns_PredecessorRunId",
                        column: x => x.PredecessorRunId,
                        principalTable: "AuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditMachineCredentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditMachineCredentials", x => x.Id);
                    table.CheckConstraint("CK_AuditCredential_Hash", "octet_length(\"SecretHash\") = 32");
                    table.ForeignKey(
                        name: "FK_AuditMachineCredentials_AuditMachines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "AuditMachines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditFindingRecords",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Finding = table.Column<byte[]>(type: "bytea", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditFindingRecords", x => new { x.RunId, x.Index });
                    table.ForeignKey(
                        name: "FK_AuditFindingRecords_AuditRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditServerSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    InputContentDigest = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FrozenAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditServerSnapshots", x => x.Id);
                    table.CheckConstraint(
                        "CK_AuditSnapshot_Bounds",
                        "octet_length(\"Bytes\") <= 536870912 AND \"Revision\" >= 0"
                    );
                    table.ForeignKey(
                        name: "FK_AuditServerSnapshots_AuditRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditCaptureRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<byte[]>(type: "bytea", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    Deadline = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    CredentialId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UploadedBytes = table.Column<long>(type: "bigint", nullable: false),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditCaptureRequests", x => x.Id);
                    table.CheckConstraint(
                        "CK_AuditRequest_Counts",
                        "\"UploadedBytes\" >= 0 AND \"UploadedBytes\" <= 536870912 AND \"RecordCount\" >= 0 AND \"RecordCount\" <= 1000000 AND \"Revision\" >= 0"
                    );
                    table.CheckConstraint(
                        "CK_AuditRequest_State",
                        "\"State\" IN ('pending','uploading','sealed','expired')"
                    );
                    table.ForeignKey(
                        name: "FK_AuditCaptureRequests_AuditMachineCredentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "AuditMachineCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_AuditCaptureRequests_AuditMachines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "AuditMachines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditEvidenceCaptures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Generation = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Manifest = table.Column<byte[]>(type: "bytea", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SealKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SealedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvidenceCaptures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvidenceCaptures_AuditCaptureRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AuditCaptureRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_AuditEvidenceCaptures_AuditMachines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "AuditMachines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditEvidenceChunks",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvidenceChunks", x => new { x.RequestId, x.Index });
                    table.CheckConstraint(
                        "CK_AuditChunk_Bounds",
                        "\"Index\" >= 0 AND \"Index\" < 10000 AND octet_length(\"Bytes\") > 0 AND octet_length(\"Bytes\") <= 8388608 AND \"RecordCount\" > 0 AND \"RecordCount\" <= 1000000"
                    );
                    table.CheckConstraint("CK_AuditChunk_Hash", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_AuditEvidenceChunks_AuditCaptureRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AuditCaptureRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "AuditRunCaptures",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRunCaptures", x => new { x.RunId, x.CaptureId });
                    table.ForeignKey(
                        name: "FK_AuditRunCaptures_AuditEvidenceCaptures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "AuditEvidenceCaptures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_AuditRunCaptures_AuditRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditCaptureRequests_CredentialId",
                table: "AuditCaptureRequests",
                column: "CredentialId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditCaptureRequests_MachineId_State_Deadline",
                table: "AuditCaptureRequests",
                columns: new[] { "MachineId", "State", "Deadline" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvidenceCaptures_ExpiresAt",
                table: "AuditEvidenceCaptures",
                column: "ExpiresAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvidenceCaptures_MachineId",
                table: "AuditEvidenceCaptures",
                column: "MachineId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvidenceCaptures_RequestId",
                table: "AuditEvidenceCaptures",
                column: "RequestId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvidenceCaptures_RequestId_SealKey",
                table: "AuditEvidenceCaptures",
                columns: new[] { "RequestId", "SealKey" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvidenceChunks_RequestId_Index_Sha256",
                table: "AuditEvidenceChunks",
                columns: new[] { "RequestId", "Index", "Sha256" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditMachineCredentials_MachineId",
                table: "AuditMachineCredentials",
                column: "MachineId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditRunCaptures_CaptureId",
                table: "AuditRunCaptures",
                column: "CaptureId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditRuns_EvaluationOfRunId",
                table: "AuditRuns",
                column: "EvaluationOfRunId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditRuns_InstanceId_IdempotencyKey",
                table: "AuditRuns",
                columns: new[] { "InstanceId", "IdempotencyKey" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditRuns_InstanceId_State_CreatedAt_Id",
                table: "AuditRuns",
                columns: new[] { "InstanceId", "State", "CreatedAt", "Id" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditRuns_PredecessorRunId",
                table: "AuditRuns",
                column: "PredecessorRunId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditRuns_SummaryExpiresAt",
                table: "AuditRuns",
                column: "SummaryExpiresAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_AuditServerSnapshots_RunId",
                table: "AuditServerSnapshots",
                column: "RunId",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AuditEvidenceChunks");

            migrationBuilder.DropTable(name: "AuditFindingRecords");

            migrationBuilder.DropTable(name: "AuditRetentionSettings");

            migrationBuilder.DropTable(name: "AuditRunCaptures");

            migrationBuilder.DropTable(name: "AuditServerSnapshots");

            migrationBuilder.DropTable(name: "AuditEvidenceCaptures");

            migrationBuilder.DropTable(name: "AuditRuns");

            migrationBuilder.DropTable(name: "AuditCaptureRequests");

            migrationBuilder.DropTable(name: "AuditMachineCredentials");

            migrationBuilder.DropTable(name: "AuditMachines");
        }
    }
}
