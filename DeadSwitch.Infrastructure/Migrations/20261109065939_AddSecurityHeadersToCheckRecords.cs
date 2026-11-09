using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DeadSwitch.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityHeadersToCheckRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MissingSecurityHeaders",
                table: "CheckRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityGrade",
                table: "CheckRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityNotAssessedReason",
                table: "CheckRecords",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MissingSecurityHeaders",
                table: "CheckRecords");

            migrationBuilder.DropColumn(
                name: "SecurityGrade",
                table: "CheckRecords");

            migrationBuilder.DropColumn(
                name: "SecurityNotAssessedReason",
                table: "CheckRecords");
        }
    }
}
