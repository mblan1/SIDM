using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIDM.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DefaultPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Extensions = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Url = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    EffectiveUrl = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CompletedUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Mime = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    LastModified = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CookiesJson = table.Column<string>(type: "TEXT", nullable: true),
                    HeadersJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExpectedHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    HashAlgo = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    SegmentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    Manifest = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduleRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartTime = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    EndTime = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    DaysOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxConcurrent = table.Column<int>(type: "INTEGER", nullable: false),
                    BandwidthKiBps = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Segments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DownloadId = table.Column<long>(type: "INTEGER", nullable: false),
                    Idx = table.Column<int>(type: "INTEGER", nullable: false),
                    StartByte = table.Column<long>(type: "INTEGER", nullable: false),
                    EndByte = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesDownloaded = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Segments_Downloads_DownloadId",
                        column: x => x.DownloadId,
                        principalTable: "Downloads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_Name",
                table: "Categories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_CreatedUtc",
                table: "Downloads",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_Status",
                table: "Downloads",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_DownloadId",
                table: "Segments",
                column: "DownloadId");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_DownloadId_Idx",
                table: "Segments",
                columns: new[] { "DownloadId", "Idx" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "ScheduleRules");

            migrationBuilder.DropTable(
                name: "Segments");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "Downloads");
        }
    }
}
