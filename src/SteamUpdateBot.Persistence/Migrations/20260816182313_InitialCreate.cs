using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF-generated migration column-name array.

namespace SteamUpdateBot.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoredGames",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    StoreUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    HeaderImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    AddedByDiscordUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AddedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ScanWatermarkUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPollAttemptUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LastPollSuccessUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    NextPollUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredGames", x => x.AppId);
                });

            migrationBuilder.CreateTable(
                name: "Announcements",
                columns: table => new
                {
                    AppId = table.Column<long>(type: "INTEGER", nullable: false),
                    SteamGid = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    GameName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    HeaderImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PublishedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DetectedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveryState = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveredAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DiscordMessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Announcements", x => new { x.AppId, x.SteamGid });
                    table.ForeignKey(
                        name: "FK_Announcements_MonitoredGames_AppId",
                        column: x => x.AppId,
                        principalTable: "MonitoredGames",
                        principalColumn: "AppId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_DeliveryState_NextAttemptUtc_PublishedAtUtc",
                table: "Announcements",
                columns: new[] { "DeliveryState", "NextAttemptUtc", "PublishedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredGames_NextPollUtc",
                table: "MonitoredGames",
                column: "NextPollUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Announcements");

            migrationBuilder.DropTable(
                name: "MonitoredGames");
        }
    }
}

#pragma warning restore CA1861
