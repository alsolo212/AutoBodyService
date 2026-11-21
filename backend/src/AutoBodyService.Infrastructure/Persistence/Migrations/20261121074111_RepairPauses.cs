using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairPauses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AppointmentStatusEvent_Status",
                table: "AppointmentStatusEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointment_Status",
                table: "Appointments");

            migrationBuilder.CreateTable(
                name: "RepairPauses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DurationDays = table.Column<int>(type: "integer", nullable: false),
                    WorkplaceReleased = table.Column<bool>(type: "boolean", nullable: false),
                    StartedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpectedReturn = table.Column<DateOnly>(type: "date", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedById = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairPauses", x => x.Id);
                    table.CheckConstraint("CK_RepairPause_Duration", "\"DurationDays\" > 0 AND \"ExpectedReturn\" > \"StartedOn\"");
                    table.ForeignKey(
                        name: "FK_RepairPauses_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepairPauses_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RepairOfferDeclines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PauseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairOfferDeclines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepairOfferDeclines_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepairOfferDeclines_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepairOfferDeclines_RepairPauses_PauseId",
                        column: x => x.PauseId,
                        principalTable: "RepairPauses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppointmentStatusEvent_Status",
                table: "AppointmentStatusEvents",
                sql: "(\"FromStatus\" BETWEEN 0 AND 6 OR \"FromStatus\" IS NULL) AND \"ToStatus\" BETWEEN 0 AND 6 AND (\"FromStatus\" IS NOT NULL OR \"ToStatus\" = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointment_Status",
                table: "Appointments",
                sql: "\"Status\" BETWEEN 0 AND 6");

            migrationBuilder.CreateIndex(
                name: "IX_RepairOfferDeclines_ActorId",
                table: "RepairOfferDeclines",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairOfferDeclines_AppointmentId",
                table: "RepairOfferDeclines",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairOfferDeclines_PauseId_AppointmentId",
                table: "RepairOfferDeclines",
                columns: new[] { "PauseId", "AppointmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepairPauses_ActorId",
                table: "RepairPauses",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairPauses_AppointmentId",
                table: "RepairPauses",
                column: "AppointmentId",
                unique: true,
                filter: "\"EndedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepairOfferDeclines");

            migrationBuilder.DropTable(
                name: "RepairPauses");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AppointmentStatusEvent_Status",
                table: "AppointmentStatusEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointment_Status",
                table: "Appointments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AppointmentStatusEvent_Status",
                table: "AppointmentStatusEvents",
                sql: "(\"FromStatus\" BETWEEN 0 AND 5 OR \"FromStatus\" IS NULL) AND \"ToStatus\" BETWEEN 0 AND 5 AND (\"FromStatus\" IS NOT NULL OR \"ToStatus\" = 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointment_Status",
                table: "Appointments",
                sql: "\"Status\" BETWEEN 0 AND 5");
        }
    }
}
