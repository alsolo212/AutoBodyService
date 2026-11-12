using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PriceCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PriceListItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    IsFrom = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsPublished = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceListItems", x => x.Id);
                    table.CheckConstraint("CK_PriceListItem_Name", "\"Name\" ~ '[^[:space:]]'");
                    table.CheckConstraint("CK_PriceListItem_Price", "\"Price\" >= 0 AND \"Price\" <> 'NaN'::numeric AND \"Price\" <> 'Infinity'::numeric AND \"Price\" <= 79228162514264337593543950335");
                    table.CheckConstraint("CK_PriceListItem_Publication", "NOT (\"IsArchived\" AND \"IsPublished\")");
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceListItems_SortOrder_Id",
                table: "PriceListItems",
                columns: new[] { "SortOrder", "Id" },
                filter: "\"IsPublished\" AND NOT \"IsArchived\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceListItems");
        }
    }
}
