using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmployeeAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedById = table.Column<string>(type: "text", nullable: false),
                    AssignedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NameSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SpecializationSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeAssignments", x => x.Id);
                    table.CheckConstraint("CK_EmployeeAssignment_ReleasedAt", "\"ReleasedAtUtc\" IS NULL OR \"ReleasedAtUtc\" >= \"AssignedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_EmployeeAssignments_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeAssignments_AspNetUsers_AssignedById",
                        column: x => x.AssignedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmployeeAssignments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAssignments_AppointmentId_AssignedAtUtc",
                table: "EmployeeAssignments",
                columns: new[] { "AppointmentId", "AssignedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAssignments_AppointmentId_EmployeeId",
                table: "EmployeeAssignments",
                columns: new[] { "AppointmentId", "EmployeeId" },
                unique: true,
                filter: "\"ReleasedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAssignments_AssignedById",
                table: "EmployeeAssignments",
                column: "AssignedById");

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAssignments_EmployeeId_AssignedAtUtc",
                table: "EmployeeAssignments",
                columns: new[] { "EmployeeId", "AssignedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmployeeAssignments");
        }
    }
}
