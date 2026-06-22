using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class AddRuteoMultitenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CajasRegistradas",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Identificador = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CallbackUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UltimaVez = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CajasRegistradas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CajasRegistradas_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProveedoresWebhookConfig",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProveedorNombre = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CuentaExternaId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CredencialesCifradas = table.Column<string>(type: "TEXT", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProveedoresWebhookConfig", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProveedoresWebhookConfig_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CajasRegistradas_TenantId_Identificador",
                table: "CajasRegistradas",
                columns: new[] { "TenantId", "Identificador" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProveedoresWebhookConfig_ProveedorNombre_CuentaExternaId",
                table: "ProveedoresWebhookConfig",
                columns: new[] { "ProveedorNombre", "CuentaExternaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProveedoresWebhookConfig_TenantId_ProveedorNombre",
                table: "ProveedoresWebhookConfig",
                columns: new[] { "TenantId", "ProveedorNombre" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CajasRegistradas");

            migrationBuilder.DropTable(
                name: "ProveedoresWebhookConfig");
        }
    }
}
