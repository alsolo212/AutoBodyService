using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReminderRecipientAndOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "ScheduledReminders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipientId",
                table: "ScheduledReminders",
                type: "text",
                nullable: true);

            // Existing client jobs already belong to the appointment owner; admin jobs
            // remain role-targeted and resolve active administrators when due.
            migrationBuilder.Sql("""
                UPDATE "ScheduledReminders" AS r
                SET "RecipientId" = a."UserId"
                FROM "Appointments" AS a
                WHERE r."AppointmentId" = a."Id" AND r."Type" IN ('Client24Hours', 'Client2Hours');
                UPDATE "ScheduledReminders" SET "Outcome" = 'ProcessedBeforeUpgrade' WHERE "ProcessedAtUtc" IS NOT NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledReminders_RecipientId",
                table: "ScheduledReminders",
                column: "RecipientId");

            migrationBuilder.AddForeignKey(
                name: "FK_ScheduledReminders_AspNetUsers_RecipientId",
                table: "ScheduledReminders",
                column: "RecipientId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ScheduledReminders_AspNetUsers_RecipientId",
                table: "ScheduledReminders");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledReminders_RecipientId",
                table: "ScheduledReminders");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "ScheduledReminders");

            migrationBuilder.DropColumn(
                name: "RecipientId",
                table: "ScheduledReminders");
        }
    }
}
