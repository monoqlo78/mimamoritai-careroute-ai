using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Device events were only ever streamed to the Fabric Eventhouse when a human
    /// hit POST /api/stream/publish, so the Eventhouse drifted arbitrarily far
    /// behind Azure SQL (the source of truth) and the Fabric Data Agent answered
    /// questions over stale data. This column lets a new background service
    /// (EventStreamPublishBackgroundService) find exactly the DeviceEvent rows that
    /// have never been published, publish them incrementally, and stamp them only
    /// on success -- so publishing is continuous, idempotent, and safely retryable.
    /// </summary>
    public partial class AddDeviceEventPublishedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedToStreamAtUtc",
                schema: "mimamori",
                table: "DeviceEvents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceEvents_PublishedToStreamAtUtc",
                schema: "mimamori",
                table: "DeviceEvents",
                column: "PublishedToStreamAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DeviceEvents_PublishedToStreamAtUtc",
                schema: "mimamori",
                table: "DeviceEvents");

            migrationBuilder.DropColumn(
                name: "PublishedToStreamAtUtc",
                schema: "mimamori",
                table: "DeviceEvents");
        }
    }
}
