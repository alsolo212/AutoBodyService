using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GuestAppointmentsAndOptionalVin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointment_Vin",
                table: "Appointments");

            migrationBuilder.AlterColumn<string>(
                name: "Vin",
                table: "Appointments",
                type: "character varying(17)",
                maxLength: 17,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(17)",
                oldMaxLength: 17);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Appointments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "CreatedById",
                table: "Appointments",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("UPDATE \"Appointments\" SET \"CreatedById\" = \"UserId\"");
            migrationBuilder.AlterColumn<string>(name: "CreatedById", table: "Appointments", type: "text",
                nullable: false, oldClrType: typeof(string), oldType: "text", oldNullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "Appointments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerPhone",
                table: "Appointments",
                type: "character varying(13)",
                maxLength: 13,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_CreatedById",
                table: "Appointments",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_UserId_CreatedAtUtc",
                table: "Appointments",
                columns: new[] { "UserId", "CreatedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointment_Customer",
                table: "Appointments",
                sql: "(\"UserId\" IS NOT NULL AND \"CustomerName\" IS NULL AND \"CustomerPhone\" IS NULL) OR (\"UserId\" IS NULL AND \"CustomerName\" IS NOT NULL AND \"CustomerPhone\" IS NOT NULL AND \"CustomerName\" ~ '[^[:space:]]' AND \"CustomerPhone\" ~ '^\\+380[0-9]{9}$')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointment_Vin",
                table: "Appointments",
                sql: "\"Vin\" IS NULL OR \"Vin\" ~ '^[A-HJ-NPR-Z0-9]{17}$'");

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_AspNetUsers_CreatedById",
                table: "Appointments",
                column: "CreatedById",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "Appointments" WHERE "UserId" IS NULL OR "Vin" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot revert: appointments without an account or VIN exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_AspNetUsers_CreatedById",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_CreatedById",
                table: "Appointments");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_UserId_CreatedAtUtc",
                table: "Appointments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointment_Customer",
                table: "Appointments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Appointment_Vin",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "CreatedById",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "CustomerPhone",
                table: "Appointments");

            migrationBuilder.AlterColumn<string>(
                name: "Vin",
                table: "Appointments",
                type: "character varying(17)",
                maxLength: 17,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(17)",
                oldMaxLength: 17,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "Appointments",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Appointment_Vin",
                table: "Appointments",
                sql: "\"Vin\" ~ '^[A-HJ-NPR-Z0-9]{17}$'");
        }
    }
}
