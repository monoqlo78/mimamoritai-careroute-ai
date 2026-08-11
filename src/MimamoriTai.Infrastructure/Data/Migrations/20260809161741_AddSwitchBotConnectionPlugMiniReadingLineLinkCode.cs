using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSwitchBotConnectionPlugMiniReadingLineLinkCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LineLinkCodes",
                schema: "mimamori",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineLinkCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LineLinkCodes_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalSchema: "mimamori",
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlugMiniReadings",
                schema: "mimamori",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoltageV = table.Column<double>(type: "float", nullable: true),
                    CurrentMa = table.Column<double>(type: "float", nullable: true),
                    DailyEnergyWh = table.Column<double>(type: "float", nullable: true),
                    UsageMinutesToday = table.Column<int>(type: "int", nullable: true),
                    ApproxWatts = table.Column<double>(type: "float", nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedToStreamAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlugMiniReadings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlugMiniReadings_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "mimamori",
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SwitchBotConnections",
                schema: "mimamori",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EncryptedToken = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    EncryptedSecret = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    LastValidatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSyncAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwitchBotConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SwitchBotConnections_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalSchema: "mimamori",
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LineLinkCodes_CodeHash_UsedAtUtc_ExpiresAtUtc",
                schema: "mimamori",
                table: "LineLinkCodes",
                columns: new[] { "CodeHash", "UsedAtUtc", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LineLinkCodes_HouseholdId",
                schema: "mimamori",
                table: "LineLinkCodes",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_PlugMiniReadings_DeviceId",
                schema: "mimamori",
                table: "PlugMiniReadings",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PlugMiniReadings_HouseholdId_DeviceId_OccurredAtUtc",
                schema: "mimamori",
                table: "PlugMiniReadings",
                columns: new[] { "HouseholdId", "DeviceId", "OccurredAtUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlugMiniReadings_PublishedToStreamAtUtc",
                schema: "mimamori",
                table: "PlugMiniReadings",
                column: "PublishedToStreamAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SwitchBotConnections_HouseholdId",
                schema: "mimamori",
                table: "SwitchBotConnections",
                column: "HouseholdId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LineLinkCodes",
                schema: "mimamori");

            migrationBuilder.DropTable(
                name: "PlugMiniReadings",
                schema: "mimamori");

            migrationBuilder.DropTable(
                name: "SwitchBotConnections",
                schema: "mimamori");
        }
    }
}
