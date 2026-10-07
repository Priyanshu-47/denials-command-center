using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AQ.Denials.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityKey = table.Column<string>(type: "text", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    RunGuid = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Claims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClaimId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PayerId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PayerName = table.Column<string>(type: "text", nullable: false),
                    PatientFirst = table.Column<string>(type: "text", nullable: false),
                    PatientLast = table.Column<string>(type: "text", nullable: false),
                    PatientDob = table.Column<string>(type: "text", nullable: false),
                    MemberId = table.Column<string>(type: "text", nullable: false),
                    Dos = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SubmittedDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RenderingNpi = table.Column<string>(type: "text", nullable: false),
                    RenderingProvider = table.Column<string>(type: "text", nullable: false),
                    Facility = table.Column<string>(type: "text", nullable: false),
                    Pos = table.Column<string>(type: "text", nullable: false),
                    AuthNumber = table.Column<string>(type: "text", nullable: true),
                    CoderId = table.Column<string>(type: "text", nullable: true),
                    PrebillReviewed = table.Column<string>(type: "text", nullable: false),
                    Charge = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Adjudicated = table.Column<bool>(type: "boolean", nullable: false),
                    CurrentStatus = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    SubmittedCharge = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    LastCheckDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    LifetimePaid = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IngestRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FullStateChecksum = table.Column<string>(type: "text", nullable: true),
                    RowsIn = table.Column<int>(type: "integer", nullable: false),
                    RowsMatched = table.Column<int>(type: "integer", nullable: false),
                    RowsExcepted = table.Column<int>(type: "integer", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    FailureMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RemitFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FileName = table.Column<string>(type: "text", nullable: false),
                    FileSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayerId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CheckDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    BprTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TraceNumber = table.Column<string>(type: "text", nullable: true),
                    IsDuplicate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    DuplicateOfFileName = table.Column<string>(type: "text", nullable: true),
                    ClaimCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemitFiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorklogEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RowNumber = table.Column<int>(type: "integer", nullable: false),
                    RawClaimReference = table.Column<string>(type: "text", nullable: false),
                    ClaimId = table.Column<string>(type: "text", nullable: true),
                    PayerName = table.Column<string>(type: "text", nullable: false),
                    DateLoggedRaw = table.Column<string>(type: "text", nullable: false),
                    DateLoggedMdy = table.Column<string>(type: "text", nullable: true),
                    DateLoggedDmy = table.Column<string>(type: "text", nullable: true),
                    DateAmbiguous = table.Column<bool>(type: "boolean", nullable: false),
                    DateLoggedConservative = table.Column<string>(type: "text", nullable: true),
                    DateResolvedByFutureTest = table.Column<bool>(type: "boolean", nullable: false),
                    AmountAsLogged = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    Owner = table.Column<string>(type: "text", nullable: false),
                    StatusRaw = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorklogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClaimLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClaimId = table.Column<int>(type: "integer", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    Cpt = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Modifier = table.Column<string>(type: "text", nullable: true),
                    Units = table.Column<int>(type: "integer", nullable: false),
                    Charge = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Dx1 = table.Column<string>(type: "text", nullable: true),
                    Dx2 = table.Column<string>(type: "text", nullable: true),
                    Dx3 = table.Column<string>(type: "text", nullable: true),
                    Dx4 = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimLines_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExceptionRows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IngestRunId = table.Column<int>(type: "integer", nullable: true),
                    ReasonCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    SourceFile = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: false),
                    ClaimId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OriginalRef = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExceptionRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExceptionRows_IngestRuns_IngestRunId",
                        column: x => x.IngestRunId,
                        principalTable: "IngestRuns",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RemitObservations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClaimId = table.Column<int>(type: "integer", nullable: false),
                    RemitFileId = table.Column<int>(type: "integer", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    PayerId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CheckDate = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RawClaimReference = table.Column<string>(type: "text", nullable: false),
                    StatusCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    SubmittedCharge = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PatientResponsibility = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    NaturalKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RemarkCodes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemitObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemitObservations_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RemitObservations_RemitFiles_RemitFileId",
                        column: x => x.RemitFileId,
                        principalTable: "RemitFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ObservationServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RemitObservationId = table.Column<int>(type: "integer", nullable: false),
                    ProcedureCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Charge = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Paid = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    RemarkCodes = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ObservationServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ObservationServices_RemitObservations_RemitObservationId",
                        column: x => x.RemitObservationId,
                        principalTable: "RemitObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Adjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RemitObservationId = table.Column<int>(type: "integer", nullable: false),
                    ObservationServiceId = table.Column<int>(type: "integer", nullable: true),
                    GroupCode = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Carc = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Adjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Adjustments_ObservationServices_ObservationServiceId",
                        column: x => x.ObservationServiceId,
                        principalTable: "ObservationServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Adjustments_RemitObservations_RemitObservationId",
                        column: x => x.RemitObservationId,
                        principalTable: "RemitObservations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Adjustments_ObservationServiceId",
                table: "Adjustments",
                column: "ObservationServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Adjustments_RemitObservationId",
                table: "Adjustments",
                column: "RemitObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_At",
                table: "AuditLog",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ClaimLines_ClaimId",
                table: "ClaimLines",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_ClaimId",
                table: "Claims",
                column: "ClaimId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionRows_ClaimId",
                table: "ExceptionRows",
                column: "ClaimId");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionRows_IngestRunId",
                table: "ExceptionRows",
                column: "IngestRunId");

            migrationBuilder.CreateIndex(
                name: "IX_ExceptionRows_ReasonCode",
                table: "ExceptionRows",
                column: "ReasonCode");

            migrationBuilder.CreateIndex(
                name: "IX_IngestRuns_FullStateChecksum",
                table: "IngestRuns",
                column: "FullStateChecksum");

            migrationBuilder.CreateIndex(
                name: "IX_IngestRuns_RunGuid",
                table: "IngestRuns",
                column: "RunGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObservationServices_RemitObservationId",
                table: "ObservationServices",
                column: "RemitObservationId");

            migrationBuilder.CreateIndex(
                name: "IX_RemitFiles_FileName",
                table: "RemitFiles",
                column: "FileName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemitFiles_PayloadSha256",
                table: "RemitFiles",
                column: "PayloadSha256");

            migrationBuilder.CreateIndex(
                name: "IX_RemitObservations_ClaimId_Seq",
                table: "RemitObservations",
                columns: new[] { "ClaimId", "Seq" });

            migrationBuilder.CreateIndex(
                name: "IX_RemitObservations_NaturalKey",
                table: "RemitObservations",
                column: "NaturalKey");

            migrationBuilder.CreateIndex(
                name: "IX_RemitObservations_RemitFileId",
                table: "RemitObservations",
                column: "RemitFileId");

            migrationBuilder.CreateIndex(
                name: "IX_WorklogEntries_RowNumber",
                table: "WorklogEntries",
                column: "RowNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Adjustments");

            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "ClaimLines");

            migrationBuilder.DropTable(
                name: "ExceptionRows");

            migrationBuilder.DropTable(
                name: "WorklogEntries");

            migrationBuilder.DropTable(
                name: "ObservationServices");

            migrationBuilder.DropTable(
                name: "IngestRuns");

            migrationBuilder.DropTable(
                name: "RemitObservations");

            migrationBuilder.DropTable(
                name: "Claims");

            migrationBuilder.DropTable(
                name: "RemitFiles");
        }
    }
}
