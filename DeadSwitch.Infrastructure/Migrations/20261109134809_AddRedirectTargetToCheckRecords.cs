using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeadSwitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRedirectTargetToCheckRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RedirectFinding",
                table: "CheckRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedirectTargetHost",
                table: "CheckRecords",
                type: "TEXT",
                maxLength: 253,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RedirectFinding",
                table: "CheckRecords");

            migrationBuilder.DropColumn(
                name: "RedirectTargetHost",
                table: "CheckRecords");
        }
    }
}
