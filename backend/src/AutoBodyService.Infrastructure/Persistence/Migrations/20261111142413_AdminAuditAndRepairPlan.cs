using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminAuditAndRepairPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminAuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ObjectType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ObjectId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminAuditEvents_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RepairPlan",
                columns: table => new
                {
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgreedPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PlannedStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EstimatedFinishUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairPlan", x => x.AppointmentId);
                    table.CheckConstraint("CK_RepairPlan_Currency", "\"Currency\" = 'UAH'");
                    table.CheckConstraint("CK_RepairPlan_Dates", "\"EstimatedFinishUtc\" >= \"PlannedStartUtc\"");
                    table.CheckConstraint("CK_RepairPlan_Price", "\"AgreedPrice\" >= 0 AND \"AgreedPrice\" <> 'NaN'::numeric AND \"AgreedPrice\" <> 'Infinity'::numeric");
                    table.ForeignKey(
                        name: "FK_RepairPlan_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_Action_OccurredAtUtc",
                table: "AdminAuditEvents",
                columns: new[] { "Action", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_ActorId_OccurredAtUtc",
                table: "AdminAuditEvents",
                columns: new[] { "ActorId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminAuditEvents_OccurredAtUtc",
                table: "AdminAuditEvents",
                column: "OccurredAtUtc");

            migrationBuilder.Sql("""
                CREATE FUNCTION prevent_admin_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Administrative audit events are append-only';
                END;
                $$;
                CREATE TRIGGER admin_audit_append_only BEFORE UPDATE OR DELETE OR TRUNCATE
                    ON "AdminAuditEvents" FOR EACH STATEMENT EXECUTE FUNCTION prevent_admin_audit_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdminAuditEvents");

            migrationBuilder.Sql("DROP FUNCTION prevent_admin_audit_mutation();");

            migrationBuilder.DropTable(
                name: "RepairPlan");
        }
    }
}
