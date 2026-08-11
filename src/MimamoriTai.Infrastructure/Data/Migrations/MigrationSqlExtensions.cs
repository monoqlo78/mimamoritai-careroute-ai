using Microsoft.EntityFrameworkCore.Migrations;

namespace MimamoriTai.Infrastructure.Data.Migrations;

internal static class MigrationSqlExtensions
{
    /// <summary>
    /// SQL Server は CREATE / ALTER VIEW をバッチの先頭にしか許さない。EF Core は 1 つの
    /// マイグレーション内の Sql() をまとめて 1 バッチとして送るため、ビューを 2 つ以上作ると
    /// 2 つめ以降が "Incorrect syntax near the keyword 'OR'" で落ちて、そのマイグレーションと
    /// 後続すべてが未適用のまま止まる。EXEC で入れ子のバッチにすればこの制約を回避できる。
    /// </summary>
    public static void SqlAsOwnBatch(this MigrationBuilder migrationBuilder, string sql)
        => migrationBuilder.Sql("EXEC(N'" + sql.Replace("'", "''") + "')");
}
