using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManualContactEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualContactEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualContactEvents", x => x.Id);
                    table.CheckConstraint("CK_ManualContactEvent_Reason", "\"Reason\" BETWEEN 0 AND 3");
                    table.ForeignKey(
                        name: "FK_ManualContactEvents_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualContactEvents_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualContactEvents_ActorId",
                table: "ManualContactEvents",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualContactEvents_AppointmentId_Reason_RecordedAtUtc",
                table: "ManualContactEvents",
                columns: new[] { "AppointmentId", "Reason", "RecordedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualContactEvents");
        }
    }
}
