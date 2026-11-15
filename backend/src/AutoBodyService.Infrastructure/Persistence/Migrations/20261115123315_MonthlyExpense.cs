using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MonthlyExpense : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonthlyExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedById = table.Column<string>(type: "text", nullable: false),
                    RecordedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedYear = table.Column<int>(type: "integer", nullable: false),
                    RecordedMonth = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthlyExpenses", x => x.Id);
                    table.CheckConstraint("CK_MonthlyExpense_Amount", "\"Amount\" > 0 AND \"Amount\" <> 'NaN'::numeric AND \"Amount\" <> 'Infinity'::numeric");
                    table.CheckConstraint("CK_MonthlyExpense_Period", "\"RecordedYear\" BETWEEN 1 AND 9999 AND \"RecordedMonth\" BETWEEN 1 AND 12");
                    table.CheckConstraint("CK_MonthlyExpense_UpdatedAt", "\"UpdatedAtUtc\" IS NULL OR \"UpdatedAtUtc\" >= \"RecordedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_MonthlyExpenses_AspNetUsers_CreatedById",
                        column: x => x.CreatedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyExpenses_CreatedById",
                table: "MonthlyExpenses",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_MonthlyExpenses_RecordedYear_RecordedMonth_RecordedAtUtc",
                table: "MonthlyExpenses",
                columns: new[] { "RecordedYear", "RecordedMonth", "RecordedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonthlyExpenses");
        }
    }
}
