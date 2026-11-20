using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RepairCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RepairCapacity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Workplaces = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairCapacity", x => x.Id);
                    table.CheckConstraint("CK_RepairCapacity_Positive", "\"Workplaces\" IS NULL OR \"Workplaces\" > 0");
                    table.CheckConstraint("CK_RepairCapacity_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.InsertData(
                table: "RepairCapacity",
                columns: new[] { "Id", "Workplaces" },
                values: new object[] { 1, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepairCapacity");
        }
    }
}
