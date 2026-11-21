using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairConflictContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ManualContactEvent_Reason",
                table: "ManualContactEvents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManualContactEvent_Reason",
                table: "ManualContactEvents",
                sql: "\"Reason\" BETWEEN 0 AND 4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ManualContactEvent_Reason",
                table: "ManualContactEvents");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ManualContactEvent_Reason",
                table: "ManualContactEvents",
                sql: "\"Reason\" BETWEEN 0 AND 3");
        }
    }
}
