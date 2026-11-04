using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeadSwitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringHistoryAndScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IntervalSeconds",
                table: "MonitoredServices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "MonitoredServices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutSeconds",
                table: "MonitoredServices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateTable(
                name: "CheckRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MonitoredServiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    CheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    StatusCode = table.Column<int>(type: "INTEGER", nullable: true),
                    ResponseTimeMs = table.Column<long>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckRecords_MonitoredServices_MonitoredServiceId",
                        column: x => x.MonitoredServiceId,
                        principalTable: "MonitoredServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckRecords_MonitoredServiceId_CheckedAtUtc",
                table: "CheckRecords",
                columns: new[] { "MonitoredServiceId", "CheckedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckRecords");

            migrationBuilder.DropColumn(
                name: "IntervalSeconds",
                table: "MonitoredServices");

            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "MonitoredServices");

            migrationBuilder.DropColumn(
                name: "TimeoutSeconds",
                table: "MonitoredServices");
        }
    }
}
