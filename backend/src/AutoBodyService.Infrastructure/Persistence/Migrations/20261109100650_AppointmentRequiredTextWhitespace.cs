using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppointmentRequiredTextWhitespace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointment_RequiredText",
                table: "Appointments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointment_RequiredText",
                table: "Appointments",
                sql: "\"Make\" ~ '[^[:space:]]' AND \"Model\" ~ '[^[:space:]]' AND \"ProblemDescription\" ~ '[^[:space:]]'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointment_RequiredText",
                table: "Appointments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointment_RequiredText",
                table: "Appointments",
                sql: "length(btrim(\"Make\")) > 0 AND length(btrim(\"Model\")) > 0 AND length(btrim(\"ProblemDescription\")) > 0");
        }
    }
}
