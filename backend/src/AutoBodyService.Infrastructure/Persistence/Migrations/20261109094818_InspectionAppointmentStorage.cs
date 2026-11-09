using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InspectionAppointmentStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    InspectionStartUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Vin = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: false),
                    Make = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    ProblemDescription = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.CheckConstraint("CK_Appointment_RequiredText", "length(btrim(\"Make\")) > 0 AND length(btrim(\"Model\")) > 0 AND length(btrim(\"ProblemDescription\")) > 0");
                    table.CheckConstraint("CK_Appointment_Status", "\"Status\" BETWEEN 0 AND 5");
                    table.CheckConstraint("CK_Appointment_Vin", "\"Vin\" ~ '^[A-HJ-NPR-Z0-9]{17}$'");
                    table.ForeignKey(
                        name: "FK_Appointments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_InspectionStartUtc",
                table: "Appointments",
                column: "InspectionStartUtc",
                unique: true,
                filter: "\"Status\" <> 5");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_UserId_InspectionStartUtc",
                table: "Appointments",
                columns: new[] { "UserId", "InspectionStartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Appointments");
        }
    }
}
