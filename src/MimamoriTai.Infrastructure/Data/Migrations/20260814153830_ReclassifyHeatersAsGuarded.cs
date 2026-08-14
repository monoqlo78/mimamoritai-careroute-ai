using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <summary>
    /// <c>DeviceSafetyPolicy.Classify</c> used to put every heat-producing appliance in
    /// <c>Restricted</c>, which refused remote turn-on outright. It now puts them in
    /// <c>Guarded</c>, which allows turn-on behind a hazard check. Rows written before
    /// that change still say Restricted, so the households that already have a heater
    /// would never see the new behaviour.
    ///
    /// <para>
    /// This only reclassifies the device types that <c>Classify</c> itself now maps to
    /// Guarded, and only rows currently sitting at Restricted. Safe devices are left
    /// alone (moving them to Guarded would add a confirmation nobody asked for), and
    /// sensors and unknown hardware stay Restricted.
    /// </para>
    ///
    /// <para>
    /// The "遠隔でONにすることを禁止する" setting also writes Restricted, so in principle
    /// this could overwrite a deliberate human choice. It cannot here: that checkbox ships
    /// in the same release as this migration, so no existing row can have been set by it.
    /// A later migration must not repeat this blanket update.
    /// </para>
    /// </summary>
    public partial class ReclassifyHeatersAsGuarded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE mimamori.Devices
                SET SafetyClass = 'Guarded'
                WHERE SafetyClass = 'Restricted'
                  AND DeviceType IN ('Plug', 'Heater', 'Kettle', 'Microwave', 'CookingDevice');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE mimamori.Devices
                SET SafetyClass = 'Restricted'
                WHERE SafetyClass = 'Guarded';
                """);
        }
    }
}
