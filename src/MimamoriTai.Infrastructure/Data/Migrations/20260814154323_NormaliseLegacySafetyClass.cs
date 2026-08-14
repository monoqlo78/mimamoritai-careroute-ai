using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Some Devices rows hold the enum's <em>numeric</em> value ("0", "1") in the
    /// SafetyClass column instead of its name. Those rows predate the column being
    /// written through the enum-to-name conversion, and they are not harmless:
    /// <c>Enum.Parse</c> happily accepts a numeric string, so a row reading "0" is loaded
    /// as <c>Safe</c>. A real SwitchBot Plug Mini was sitting in exactly that state -
    /// remote-control enabled, and treated as safe to switch on with no hazard check,
    /// even though nothing tells us what is plugged into it.
    ///
    /// <para>
    /// Recompute the class from DeviceType (mirroring
    /// <c>DeviceSafetyPolicy.Classify</c>) for every row whose value is not one of the
    /// three legal names, so unparseable data always lands on the cautious side rather
    /// than silently on Safe.
    /// </para>
    /// </summary>
    public partial class NormaliseLegacySafetyClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE mimamori.Devices
                SET SafetyClass =
                    CASE
                        WHEN DeviceType IN ('Light', 'Fan', 'DemoDevice') THEN 'Safe'
                        WHEN DeviceType IN ('Plug', 'Heater', 'Kettle', 'Microwave', 'CookingDevice') THEN 'Guarded'
                        ELSE 'Restricted'
                    END
                WHERE SafetyClass NOT IN ('Safe', 'Guarded', 'Restricted');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Deliberately empty: restoring values that could not be parsed would put the
            // "loaded as Safe" hole back.
        }
    }
}
