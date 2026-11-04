using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeadSwitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeCurrentStatusRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CurrentStatus",
                table: "MonitoredServices",
                type: "TEXT",
                nullable: false,
                defaultValue: "UNKNOWN",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CurrentStatus",
                table: "MonitoredServices",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldDefaultValue: "UNKNOWN");
        }
    }
}
