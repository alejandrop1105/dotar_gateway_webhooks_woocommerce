using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dotar.Gateway.Migrations
{
    /// <inheritdoc />
    public partial class AgregarRuteoProveedorWooCommerceMultiSucursal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProveedorRuteoNombre",
                table: "Tenants",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RuteoProveedorActivo",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SucursalMetaKey",
                table: "Tenants",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SucursalMetaSeparador",
                table: "Tenants",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProveedorRuteoNombre",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RuteoProveedorActivo",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SucursalMetaKey",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SucursalMetaSeparador",
                table: "Tenants");
        }
    }
}
