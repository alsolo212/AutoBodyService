using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InspectionDateExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InspectionDateOverrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionDateOverrides", x => x.Id);
                    table.CheckConstraint("CK_InspectionDateOverride_Mode", "\"Mode\" BETWEEN 0 AND 2");
                });

            migrationBuilder.CreateTable(
                name: "InspectionDateInterval",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OverrideId = table.Column<Guid>(type: "uuid", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    End = table.Column<TimeOnly>(type: "time without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionDateInterval", x => x.Id);
                    table.CheckConstraint("CK_InspectionDateInterval_Valid", "\"Start\" < \"End\"");
                    table.ForeignKey(
                        name: "FK_InspectionDateInterval_InspectionDateOverrides_OverrideId",
                        column: x => x.OverrideId,
                        principalTable: "InspectionDateOverrides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InspectionSlotOverride",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OverrideId = table.Column<Guid>(type: "uuid", nullable: false),
                    Start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionSlotOverride", x => x.Id);
                    table.CheckConstraint("CK_InspectionSlotOverride_Mode", "\"Mode\" BETWEEN 0 AND 1");
                    table.ForeignKey(
                        name: "FK_InspectionSlotOverride_InspectionDateOverrides_OverrideId",
                        column: x => x.OverrideId,
                        principalTable: "InspectionDateOverrides",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionDateInterval_OverrideId",
                table: "InspectionDateInterval",
                column: "OverrideId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionDateOverrides_Date",
                table: "InspectionDateOverrides",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InspectionSlotOverride_OverrideId_Start",
                table: "InspectionSlotOverride",
                columns: new[] { "OverrideId", "Start" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InspectionDateInterval");

            migrationBuilder.DropTable(
                name: "InspectionSlotOverride");

            migrationBuilder.DropTable(
                name: "InspectionDateOverrides");
        }
    }
}
