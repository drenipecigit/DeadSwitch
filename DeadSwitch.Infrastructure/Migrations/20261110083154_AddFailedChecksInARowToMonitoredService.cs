using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeadSwitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFailedChecksInARowToMonitoredService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedChecksInARow",
                table: "MonitoredServices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedChecksInARow",
                table: "MonitoredServices");
        }
    }
}
