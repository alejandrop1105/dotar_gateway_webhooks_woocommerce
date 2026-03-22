using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TenantGroupId",
                table: "Tenants",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RetryPolicyId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantGroups_RetryPolicies_RetryPolicyId",
                        column: x => x.RetryPolicyId,
                        principalTable: "RetryPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 22, 13, 46, 26, 126, DateTimeKind.Utc).AddTicks(715));

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_TenantGroupId",
                table: "Tenants",
                column: "TenantGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantGroups_RetryPolicyId",
                table: "TenantGroups",
                column: "RetryPolicyId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tenants_TenantGroups_TenantGroupId",
                table: "Tenants",
                column: "TenantGroupId",
                principalTable: "TenantGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tenants_TenantGroups_TenantGroupId",
                table: "Tenants");

            migrationBuilder.DropTable(
                name: "TenantGroups");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_TenantGroupId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TenantGroupId",
                table: "Tenants");

            migrationBuilder.UpdateData(
                table: "RetryPolicies",
                keyColumn: "Id",
                keyValue: 1,
                column: "CreatedAt",
                value: new DateTime(2026, 3, 21, 23, 6, 35, 321, DateTimeKind.Utc).AddTicks(1069));
        }
    }
}
