using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RevokeSignedOutSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RevokedSessions",
                columns: table => new
                {
                    IdHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevokedSessions", x => x.IdHash);
                    table.ForeignKey(
                        name: "FK_RevokedSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RevokedSessions_ExpiresUtc",
                table: "RevokedSessions",
                column: "ExpiresUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RevokedSessions_UserId",
                table: "RevokedSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RevokedSessions");
        }
    }
}
