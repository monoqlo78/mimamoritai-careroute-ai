# Microsoft Fabric Data Agent セットアップ

このアプリの「生活データ質問」機能（例:「今日最初に活動したのは何時？」）は、`IFabricDataAgentClient` が設定されていれば Microsoft Fabric の Data Agent に問い合わせ、未設定の場合はアプリ内蔵の `LocalDataQuestionService` がDBから直接キーワードマッチで回答します（`AssistantOrchestrator.HandleQueryAsync`）。

## 現状の実装状況

- 現在DIに登録されているのは `MockFabricDataAgentClient` のみで、常に `IsConfigured = false` を返します。**実際にFabric Data Agent(MCP)へ接続するクライアントは未実装**です。
- `FabricOptions`（`src/MimamoriTai.Infrastructure/Fabric/FabricOptions.cs`）には `Enabled`, `WorkspaceId`, `DataAgentId`, `McpUrl`, `Scope` が定義済みで、認証は `Azure.Identity`（`DefaultAzureCredential` / ローカルでは Azure CLI ログイン、Azure上ではManaged Identity）を使う設計コメントがありますが、**実装コード自体はまだありません**（要確認: 実装予定のクラス名等は未決定）。
- 実装する際は `src/MimamoriTai.Infrastructure/Fabric/` に `FabricDataAgentClient : IFabricDataAgentClient` を追加し、`ServiceCollectionExtensions.cs` の Fabric セクションで `FabricOptions.IsConfigured` の分岐を追加してください（現状はモック固定登録）。
- ※未検証：以下の「1. ワークスペースの作成」〜「3. Data Agent の作成」の手順・画面名は、実際のFabricポータルで操作を確認したものではなく一般的な知識をもとに記載しています。Fabricの機能・UIは頻繁に更新されるため、実施前に必ず最新のFabricポータル・公式ドキュメント（`docs/REFERENCES.md`）で確認してください。

## 1. ワークスペースの作成

1. [Microsoft Fabric](https://app.fabric.microsoft.com) にサインインします。
2. 左下の「ワークスペース」→「新しいワークスペースの作成」から、見守り隊専用のワークスペースを作成します（Fabric容量が必要）。

## 2. SQLデータのミラーリング／取り込み

本アプリのデータは SQL Server（またはSQLite）に保存されています。Fabricから参照可能にする方法は主に2通りです。

- **Azure SQL Database のミラーリング**: SQL Server が Azure SQL Database の場合、Fabricの「Mirrored Azure SQL Database」機能でほぼリアルタイムにOneLakeへ複製できます。
- **Data Pipeline / Dataflow による定期取り込み**: オンプレミスSQL Server や SQLite ファイルの場合は、Fabric Data Pipeline や Dataflow Gen2 でLakehouse/Warehouseへ定期コピーします。

対象テーブルの例: `DeviceEvents`, `DailyActivitySummaries`（未実装のためビューで代用可）, `Devices`, `People`, `RiskAssessments`。

## 3. Data Agent の作成

1. Fabricワークスペース内で「+ 新規」→「Data Agent（AI skill）」を作成します。
2. データソースとして、上記でミラーリング／取り込みしたLakehouseまたはWarehouseを追加します。
3. Data Agent の詳細画面から、MCP（Model Context Protocol）エンドポイントURLを取得し、`Fabric:McpUrl` に設定します。

## 4. 推奨 Data Agent 指示（instructions）

Data Agentの「指示」欄に、以下のような日本語プロンプトを設定することを推奨します（要調整: 実際のテーブル名・カラム名に合わせて書き換えてください）。

```
あなたは高齢者見守りサービス「見守り隊」のデータ分析アシスタントです。
DeviceEvents テーブル（家電のON/OFFイベント）と Devices テーブル（機器情報）、
People テーブル（世帯構成員）を使って、家族からの生活リズムに関する質問に答えてください。

回答のルール:
- 時刻はすべて Asia/Tokyo（JST）に変換してから回答すること。OccurredAtUtc はUTCで保存されています。
- 深夜帯とは 00:00〜05:00(JST) を指します。
- 「活動」とは State = 'on' のイベントを指します。
- 個人が特定できるような機微な情報（位置情報等）を断定的に決めつけず、
  データから読み取れる事実のみを簡潔な日本語で回答してください。
- データが不足している場合は、正直に「データが不足しています」と答えてください。
- 医療的な診断や断定は行わず、あくまで生活リズムの傾向として伝えてください。
```

## 5. 想定質問例（動作確認用）

- 「今日最初に活動したのは何時？」
- 「先週と比べて活動量はどう変わった？」
- 「深夜に家電を使った日はありましたか？」
- 「直近2週間で一番活動が少なかった日は？」
- 「今日のお母さんの様子を教えて」

これらは現在 `LocalDataQuestionService`（`src/MimamoriTai.Core/Application/LocalDataQuestionService.cs`）がキーワードマッチで簡易的にカバーしている質問と同じ種類のものです。Fabric Data Agentを実装・接続した後も、`AssistantOrchestrator.HandleQueryAsync` は Fabric が失敗した場合に自動的にこのローカル回答へフォールバックするため、Fabricの回答精度が不十分な場合でもデモが破綻しない設計になっています。

## 実クライアントの接続先

実装が完了したら、次の設定を行ってください（プレースホルダーのみ・実際の値は絶対にコミットしないこと）。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Fabric:WorkspaceId" "<your-fabric-workspace-id>"
dotnet user-secrets set "Fabric:DataAgentId" "<your-fabric-data-agent-id>"
dotnet user-secrets set "Fabric:McpUrl" "<your-fabric-data-agent-mcp-url>"
```

`Fabric:Enabled` を `true` にするのを忘れないでください（`appsettings.Development.json` または環境変数 `Fabric__Enabled=true`）。
