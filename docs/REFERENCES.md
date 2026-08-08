# 参考資料

以下はドキュメント作成にあたり参照した、または参照を推奨する公式資料です。可能な限り一次情報（公式ドキュメント）にリンクしていますが、リンク先の内容そのものは今回すべてを実際にアクセスして検証したわけではありません。**未検証のものには「（要確認）」を明記しています。**

## .NET / ASP.NET Core / EF Core

- [.NET 10 の新機能](https://learn.microsoft.com/dotnet/core/whats-new/dotnet-10/overview) — （要確認: 最新の正式名称・バージョン情報は公開時期により変わる可能性）
- [ASP.NET Core Blazor の概要](https://learn.microsoft.com/aspnet/core/blazor/)
- [ASP.NET Core Blazor のレンダリングモード（InteractiveServer 含む）](https://learn.microsoft.com/aspnet/core/blazor/components/render-modes)
- [ASP.NET Core Minimal APIs の概要](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis)
- [EF Core の概要](https://learn.microsoft.com/ef/core/)
- [EF Core マイグレーション](https://learn.microsoft.com/ef/core/managing-schemas/migrations/)
- [EF Core での SQLite プロバイダー](https://learn.microsoft.com/ef/core/providers/sqlite/)
- [.NET Secret Manager (`dotnet user-secrets`) を使った開発時シークレットの管理](https://learn.microsoft.com/aspnet/core/security/app-secrets)

## SwitchBot API

- [SwitchBot API（GitHub, OpenWonderLabs/SwitchBotAPI）](https://github.com/OpenWonderLabs/SwitchBotAPI) — v1.1 の認証方式（HMAC-SHA256署名）およびエンドポイント仕様の一次情報。**本ドキュメント内のレスポンスJSONの具体的なフィールド名は実機で未検証のため「要確認」としています**（`docs/SWITCHBOT_SETUP.md` 参照）。

## OrcaRouter

- OrcaRouter の公開ドキュメントは未確認です。`OrcaRouterOptions.BaseUrl` の既定値 `https://api.orcarouter.ai/v1` と、レスポンスヘッダー名 `X-Orca-Router` / `X-Orca-Resolved-Model`（`src/MimamoriTai.Infrastructure/Ai/OrcaRouterClient.cs`）は、社内の仕様書ベースで実装したものであり、**実際のライブAPIに対して検証されていません（※未検証：実APIでの確認が必要）**。接続する前に、実際のOrcaRouterエンドポイントとレスポンス仕様を必ず確認してください。

## LINE Messaging API

- [LINE Developers ドキュメント（Messaging API）](https://developers.line.biz/ja/docs/messaging-api/overview/)
- [Webhookイベントオブジェクト](https://developers.line.biz/ja/reference/messaging-api/#webhook-event-objects)
- [署名検証（Signature validation）](https://developers.line.biz/ja/reference/messaging-api/#signature-validation) — 本アプリの `LineSignature.Verify` 実装（HMAC-SHA256 + Base64 + 定数時間比較）はこの仕様に基づいています。

## Microsoft Fabric

- [Microsoft Fabric のドキュメント](https://learn.microsoft.com/fabric/)
- [Fabric Data Agent（AI skill）の概要](https://learn.microsoft.com/fabric/data-science/concept-data-agent) — （要確認: 機能名・UI手順は本ドキュメント作成時点のものであり、Fabricの機能は頻繁に更新されるため最新のコンソールで手順が変わっている可能性があります）
- [Fabric Mirroring（Azure SQL Database等）](https://learn.microsoft.com/fabric/mirroring/overview)
- [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) — Fabric Data Agent が公開するMCPエンドポイントの背景となるプロトコル仕様。

## その他

- [Azure.Identity ライブラリ（DefaultAzureCredential）](https://learn.microsoft.com/dotnet/api/azure.identity.defaultazurecredential) — Fabric実装時に想定している認証方式（現状未実装、`docs/FABRIC_SETUP.md` 参照）。
- [xUnit ドキュメント](https://xunit.net/) — `tests/MimamoriTai.Tests` で使用しているテストフレームワーク。

---

このドキュメント一式（README.md および `docs/` 配下）は、リポジトリ内の実際のソースコードを読んだ上で作成しています。外部APIの詳細仕様（特にSwitchBotのレスポンスJSON構造とFabric Data Agentの最新UI手順）については、実装・作業前に必ず公式ドキュメントで再確認してください。
