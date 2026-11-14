using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NotificationAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Audience",
                table: "Notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Backfill only event types that existed before this migration. Unknown
            // historical types remain restricted until explicitly classified.
            migrationBuilder.Sql("""
                UPDATE "Notifications" SET "Audience" = 1 WHERE "Type" IN (
                    'AppointmentCreated', 'AppointmentCancelled', 'AppointmentScheduled',
                    'AppointmentInProgress', 'AppointmentAwaitingPayment', 'AppointmentCompleted',
                    'RepairPlanChanged', 'PasswordChanged', 'InspectionReminder24Hours', 'InspectionReminder2Hours');
                UPDATE "Notifications" SET "Audience" = 2 WHERE "Type" IN (
                    'AdminAppointmentCreated', 'AdminAppointmentCancelled', 'AdminReadyForPayment',
                    'AdminContactAgreement', 'AdminContactPlanChanged', 'AdminCalendarConflict',
                    'AdminInspectionReminder30Minutes');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notifications_Audience",
                table: "Notifications",
                sql: "\"Audience\" BETWEEN 0 AND 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Notifications_Audience",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Audience",
                table: "Notifications");
        }
    }
}
