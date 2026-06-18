using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SystemLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    TenantSlug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    WebhookEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeliveryLogId = table.Column<long>(type: "INTEGER", nullable: true),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ResponseBody = table.Column<string>(type: "TEXT", nullable: true),
                    Details = table.Column<string>(type: "TEXT", nullable: true),
                    Exception = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemLogs", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 30, 18, 12, 50, 641, DateTimeKind.Utc).AddTicks(9050));

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_Category",
                table: "SystemLogs",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_CreatedAt",
                table: "SystemLogs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_DeliveryLogId",
                table: "SystemLogs",
                column: "DeliveryLogId");

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_Level",
                table: "SystemLogs",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_TenantSlug",
                table: "SystemLogs",
                column: "TenantSlug");

            migrationBuilder.CreateIndex(
                name: "IX_SystemLogs_WebhookEventId",
                table: "SystemLogs",
                column: "WebhookEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SystemLogs");

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 13, 52, 47, 864, DateTimeKind.Utc).AddTicks(3683));
        }
    }
}
