using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeliveryLogId = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    IsManual = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryAttempts_DeliveryLogs_DeliveryLogId",
                        column: x => x.DeliveryLogId,
                        principalTable: "DeliveryLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 21, 23, 6, 35, 321, DateTimeKind.Utc).AddTicks(1069));

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryAttempts_DeliveryLogId",
                table: "DeliveryAttempts",
                column: "DeliveryLogId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveryAttempts");

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 19, 16, 42, 34, 799, DateTimeKind.Utc).AddTicks(6603));
        }
    }
}
