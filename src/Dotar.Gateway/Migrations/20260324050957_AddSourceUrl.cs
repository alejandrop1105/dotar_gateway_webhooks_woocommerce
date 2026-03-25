using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "DeliveryLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 24, 5, 9, 55, 865, DateTimeKind.Utc).AddTicks(1276));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "DeliveryLogs");

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 13, 46, 26, 126, DateTimeKind.Utc).AddTicks(715));
        }
    }
}
