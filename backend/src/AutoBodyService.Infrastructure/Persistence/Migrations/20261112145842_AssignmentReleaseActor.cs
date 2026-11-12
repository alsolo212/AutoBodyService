using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AssignmentReleaseActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReleasedById",
                table: "EmployeeAssignments",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAssignments_ReleasedById",
                table: "EmployeeAssignments",
                column: "ReleasedById");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EmployeeAssignment_ReleasedBy",
                table: "EmployeeAssignments",
                sql: "\"ReleasedById\" IS NULL OR \"ReleasedAtUtc\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_EmployeeAssignments_AspNetUsers_ReleasedById",
                table: "EmployeeAssignments",
                column: "ReleasedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EmployeeAssignments_AspNetUsers_ReleasedById",
                table: "EmployeeAssignments");

            migrationBuilder.DropIndex(
                name: "IX_EmployeeAssignments_ReleasedById",
                table: "EmployeeAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_EmployeeAssignment_ReleasedBy",
                table: "EmployeeAssignments");

            migrationBuilder.DropColumn(
                name: "ReleasedById",
                table: "EmployeeAssignments");
        }
    }
}
