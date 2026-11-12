using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Smena.Server.Database.Migrations
{
    /// <inheritdoc />
    public partial class PhotoPastedAtCashier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "pasted_at_cashier",
                table: "photos",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "ck_photos_pasted_not_stale",
                table: "photos",
                sql: "NOT (pasted_at_cashier AND is_stale)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_photos_pasted_not_stale",
                table: "photos");

            migrationBuilder.DropColumn(
                name: "pasted_at_cashier",
                table: "photos");
        }
    }
}
