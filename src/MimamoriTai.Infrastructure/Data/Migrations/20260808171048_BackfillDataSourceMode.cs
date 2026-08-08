using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MimamoriTai.Infrastructure.Data.Migrations
{
    /// <summary>
    /// The AddUsersAndDataSourceMode migration created Households.DataSourceMode as
    /// nvarchar(32) with an empty-string default, so households that already existed
    /// were left with a value that maps to no <c>DataSourceMode</c> member. That made
    /// every access check and default-household lookup return nothing. Backfill those
    /// rows to Sample and make Sample the column default going forward.
    /// </summary>
    public partial class BackfillDataSourceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE mimamori.Households
                SET DataSourceMode = 'Sample'
                WHERE DataSourceMode IS NULL
                   OR DataSourceMode NOT IN ('Sample', 'Production');
                """);

            migrationBuilder.AlterColumn<string>(
                name: "DataSourceMode",
                schema: "mimamori",
                table: "Households",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Sample",
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldNullable: false,
                oldDefaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "DataSourceMode",
                schema: "mimamori",
                table: "Households",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldNullable: false,
                oldDefaultValue: "Sample");
        }
    }
}
