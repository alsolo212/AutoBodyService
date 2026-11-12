using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminOverviewIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AppointmentStatusEvents_ToStatus_ChangedAtUtc",
                table: "AppointmentStatusEvents",
                columns: new[] { "ToStatus", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_CreatedAtUtc",
                table: "Appointments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_Status_InspectionStartUtc",
                table: "Appointments",
                columns: new[] { "Status", "InspectionStartUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppointmentStatusEvents_ToStatus_ChangedAtUtc",
                table: "AppointmentStatusEvents");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_CreatedAtUtc",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_Status_InspectionStartUtc",
                table: "Appointments");
        }
    }
}
