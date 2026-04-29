using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class AddForwardedHeaders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ForwardedHeadersJson",
                table: "DeliveryLogs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 13, 52, 47, 864, DateTimeKind.Utc).AddTicks(3683));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForwardedHeadersJson",
                table: "DeliveryLogs");

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 4, 28, 18, 47, 0, 153, DateTimeKind.Utc).AddTicks(5900));
        }
    }
}
