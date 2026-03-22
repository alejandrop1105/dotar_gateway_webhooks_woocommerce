using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetryPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CircuitBreakerThreshold = table.Column<int>(type: "INTEGER", nullable: false),
                    CircuitBreakerDurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetryPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetrySteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RetryPolicyId = table.Column<int>(type: "INTEGER", nullable: false),
                    StepNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DelayValue = table.Column<int>(type: "INTEGER", nullable: false),
                    DelayUnit = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetrySteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RetrySteps_RetryPolicies_RetryPolicyId",
                        column: x => x.RetryPolicyId,
                        principalTable: "RetryPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WebhookSecret = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RetryPolicyId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tenants_RetryPolicies_RetryPolicyId",
                        column: x => x.RetryPolicyId,
                        principalTable: "RetryPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    WebhookEventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    TargetUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    HttpStatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    AttemptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CurrentStep = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRetryAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "RetryPolicies",
                columns: new[] { "Id", "CircuitBreakerDurationSeconds", "CircuitBreakerThreshold", "CreatedAt", "IsDefault", "Name" },
                values: new object[] { 1, 30, 5, new DateTime(2026, 3, 19, 16, 42, 34, 799, DateTimeKind.Utc).AddTicks(6603), true, "Estándar" });

            migrationBuilder.InsertData(
                table: "RetrySteps",
                columns: new[] { "Id", "DelayUnit", "DelayValue", "RetryPolicyId", "StepNumber" },
                values: new object[,]
                {
                    { 1, "Seconds", 5, 1, 1 },
                    { 2, "Seconds", 30, 1, 2 },
                    { 3, "Minutes", 2, 1, 3 },
                    { 4, "Minutes", 15, 1, 4 },
                    { 5, "Hours", 1, 1, 5 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppSettings_Key",
                table: "AppSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLogs_NextRetryAt",
                table: "DeliveryLogs",
                column: "NextRetryAt");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLogs_Status",
                table: "DeliveryLogs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLogs_TenantId_CreatedAt",
                table: "DeliveryLogs",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryLogs_WebhookEventId",
                table: "DeliveryLogs",
                column: "WebhookEventId");

            migrationBuilder.CreateIndex(
                name: "IX_RetrySteps_RetryPolicyId_StepNumber",
                table: "RetrySteps",
                columns: new[] { "RetryPolicyId", "StepNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_RetryPolicyId",
                table: "Tenants",
                column: "RetryPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "DeliveryLogs");

            migrationBuilder.DropTable(
                name: "RetrySteps");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "RetryPolicies");
        }
    }
}
