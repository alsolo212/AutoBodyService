using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentInitialStatusEvent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppointmentStatusEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: true),
                    ToStatus = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentStatusEvents", x => x.Id);
                    table.CheckConstraint("CK_AppointmentStatusEvent_Status", "(\"FromStatus\" BETWEEN 0 AND 5 OR \"FromStatus\" IS NULL) AND \"ToStatus\" BETWEEN 0 AND 5 AND (\"FromStatus\" IS NOT NULL OR \"ToStatus\" = 0)");
                    table.ForeignKey(
                        name: "FK_AppointmentStatusEvents_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppointmentStatusEvents_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Existing appointments predate status history; preserve their creation facts.
            migrationBuilder.Sql("""
                INSERT INTO "AppointmentStatusEvents" ("Id", "AppointmentId", "FromStatus", "ToStatus", "ActorId", "ChangedAtUtc", "Reason")
                SELECT gen_random_uuid(), "Id", NULL, 0, "UserId", "CreatedAtUtc", NULL
                FROM "Appointments";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentStatusEvents_ActorId",
                table: "AppointmentStatusEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentStatusEvents_AppointmentId",
                table: "AppointmentStatusEvents",
                column: "AppointmentId",
                unique: true,
                filter: "\"FromStatus\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentStatusEvents_AppointmentId_ChangedAtUtc",
                table: "AppointmentStatusEvents",
                columns: new[] { "AppointmentId", "ChangedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppointmentStatusEvents");
        }
    }
}
