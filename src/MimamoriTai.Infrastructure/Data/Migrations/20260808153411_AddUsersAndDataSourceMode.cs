using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersAndDataSourceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DataSourceMode",
                schema: "mimamori",
                table: "Households",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AppUsers",
                schema: "mimamori",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdentityProvider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ExternalSubject = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    LineUserId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastLoginAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HouseholdMembers",
                schema: "mimamori",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AppUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HouseholdMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HouseholdMembers_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalSchema: "mimamori",
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HouseholdMembers_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalSchema: "mimamori",
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_IdentityProvider_ExternalSubject",
                schema: "mimamori",
                table: "AppUsers",
                columns: new[] { "IdentityProvider", "ExternalSubject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUsers_LineUserId",
                schema: "mimamori",
                table: "AppUsers",
                column: "LineUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMembers_AppUserId",
                schema: "mimamori",
                table: "HouseholdMembers",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdMembers_HouseholdId_AppUserId",
                schema: "mimamori",
                table: "HouseholdMembers",
                columns: new[] { "HouseholdId", "AppUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HouseholdMembers",
                schema: "mimamori");

            migrationBuilder.DropTable(
                name: "AppUsers",
                schema: "mimamori");

            migrationBuilder.DropColumn(
                name: "DataSourceMode",
                schema: "mimamori",
                table: "Households");
        }
    }
}
