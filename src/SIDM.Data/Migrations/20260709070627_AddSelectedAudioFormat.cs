using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSelectedAudioFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SelectedAudioFormat",
                table: "Downloads",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelectedAudioFormat",
                table: "Downloads");
        }
    }
}
