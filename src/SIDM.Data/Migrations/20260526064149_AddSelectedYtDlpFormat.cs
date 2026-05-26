using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedYtDlpFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedYtDlpFormat",
                table: "Downloads",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedYtDlpFormat",
                table: "Downloads");
        }
    }
}
