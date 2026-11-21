using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoBodyService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StatusCorrectionAndPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_PaymentHandoverConfirmations",
                table: "PaymentHandoverConfirmations");

            migrationBuilder.AddColumn<Guid>(
                name: "Id",
                table: "PaymentHandoverConfirmations",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.AddColumn<bool>(
                name: "IsCorrection",
                table: "AppointmentStatusEvents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NewInspectionStartUtc",
                table: "AppointmentStatusEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviousInspectionStartUtc",
                table: "AppointmentStatusEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_PaymentHandoverConfirmations",
                table: "PaymentHandoverConfirmations",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "RepairPriceChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OldPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    NewPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActorId = table.Column<string>(type: "text", nullable: false),
                    ChangedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepairPriceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RepairPriceChanges_Appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "Appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepairPriceChanges_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentHandoverConfirmations_AppointmentId",
                table: "PaymentHandoverConfirmations",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairPriceChanges_ActorId",
                table: "RepairPriceChanges",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_RepairPriceChanges_AppointmentId_ChangedAtUtc",
                table: "RepairPriceChanges",
                columns: new[] { "AppointmentId", "ChangedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RepairPriceChanges");

            migrationBuilder.DropPrimaryKey(
                name: "PK_PaymentHandoverConfirmations",
                table: "PaymentHandoverConfirmations");

            migrationBuilder.DropIndex(
                name: "IX_PaymentHandoverConfirmations_AppointmentId",
                table: "PaymentHandoverConfirmations");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "PaymentHandoverConfirmations");

            migrationBuilder.DropColumn(
                name: "IsCorrection",
                table: "AppointmentStatusEvents");

            migrationBuilder.DropColumn(
                name: "NewInspectionStartUtc",
                table: "AppointmentStatusEvents");

            migrationBuilder.DropColumn(
                name: "PreviousInspectionStartUtc",
                table: "AppointmentStatusEvents");

            migrationBuilder.AddPrimaryKey(
                name: "PK_PaymentHandoverConfirmations",
                table: "PaymentHandoverConfirmations",
                column: "AppointmentId");
        }
    }
}
