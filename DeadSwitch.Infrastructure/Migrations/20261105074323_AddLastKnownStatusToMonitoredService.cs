using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeadSwitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLastKnownStatusToMonitoredService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastKnownStatus",
                table: "MonitoredServices",
                type: "TEXT",
                nullable: true);

            // An existing service keeps what is already known, or its next change would give no notification.
            // Unknown stays NULL, because this column only ever holds Up or Down.
            migrationBuilder.Sql("UPDATE MonitoredServices SET LastKnownStatus = CurrentStatus WHERE CurrentStatus IN ('UP', 'DOWN');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastKnownStatus",
                table: "MonitoredServices");
        }
    }
}
