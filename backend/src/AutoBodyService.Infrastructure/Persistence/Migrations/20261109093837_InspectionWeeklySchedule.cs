using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InspectionWeeklySchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InspectionSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionSchedules", x => x.Id);
                    table.CheckConstraint("CK_InspectionSchedule_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "InspectionWeeklyHours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<int>(type: "integer", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    End = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionWeeklyHours", x => x.Id);
                    table.CheckConstraint("CK_InspectionWeeklyHours_Valid", "\"DayOfWeek\" BETWEEN 0 AND 6 AND \"Start\" < \"End\"");
                    table.ForeignKey(
                        name: "FK_InspectionWeeklyHours_InspectionSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "InspectionSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "InspectionSchedules",
                columns: new[] { "Id", "Version" },
                values: new object[] { 1, new Guid("b1050732-661b-48b8-a1bf-11d338a4b001") });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionWeeklyHours_ScheduleId",
                table: "InspectionWeeklyHours",
                column: "ScheduleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InspectionWeeklyHours");

            migrationBuilder.DropTable(
                name: "InspectionSchedules");
        }
    }
}
