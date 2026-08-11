# Microsoft Fabric Data Agent セットアップ

このアプリの「生活データ質問」機能（例:「今日最初に活動したのは何時？」）は、`IFabricDataAgentClient` が設定されていれば Microsoft Fabric の Data Agent に問い合わせ、未設定の場合はアプリ内蔵の `LocalDataQuestionService` がDBから直接キーワードマッチで回答します（`AssistantOrchestrator.HandleQueryAsync`）。

## 現状の実装状況

- `src/MimamoriTai.Infrastructure/Fabric/FabricDataAgentMcpClient.cs` が `IFabricDataAgentClient` の実クライアントです。MCP（Model Context Protocol）のJSON-RPC 2.0メッセージ（`initialize` → `notifications/initialized` → `tools/list` → `tools/call`）をHTTP経由で送受信し、レスポンスが `application/json` / `text/event-stream`（SSEの`data:`行）のどちらで返っても解釈します。認証は `EventhouseStreamPublisher` と同じ `Azure.Identity` の `TokenCredential`（`DefaultAzureCredential`）を使い、スコープ `https://api.fabric.microsoft.com/.default` のトークンをキャッシュして再利用します。
- `ServiceCollectionExtensions.cs` は `FabricOptions.IsConfigured` が true の場合のみ `FabricDataAgentMcpClient` を登録し、それ以外は `MockFabricDataAgentClient`（常に `IsConfigured = false`）を登録します。Fabricへの呼び出しが失敗した場合（未接続・容量停止中・不正なレスポンス等）も例外は投げず、`FabricAnswer(Success:false)` を返して `AssistantOrchestrator` が `LocalDataQuestionService` にフォールバックします。
- **実際にデプロイ済みのFabric Data Agent**（ワークスペースID・Data AgentIDはシークレットではないため下記に記載。実際の接続にはApp Serviceのアプリ設定で `Fabric:Enabled=true` にする必要があります）:
  - Workspace ID: `e2a48a60-0b5f-421f-91bb-51a33fe528bc`
  - Data Agent ID: `bd915a90-2bc1-4a4f-bcae-749622366f97`
  - MCP endpoint: `https://api.fabric.microsoft.com/v1/mcp/workspaces/e2a48a60-0b5f-421f-91bb-51a33fe528bc/dataagents/bd915a90-2bc1-4a4f-bcae-749622366f97/agent`
  - データソース: Kusto/KQLデータベース `MimamoriEventhouse` のテーブル `DeviceEvents`（列: EventId, HouseholdId, DeviceId, DeviceName, Room, DeviceType, EventType, State, PowerWatts, Source, OccurredAtUtc）。
  - `appsettings.json` にはこれらのGUIDをハードコードしていません（`WorkspaceId`/`DataAgentId`/`McpUrl` は空文字のまま）。環境ごとにApp Serviceのアプリ設定または `dotnet user-secrets` で注入してください。
- ※未検証：以下の「1. ワークスペースの作成」〜「3. Data Agent の作成」の手順・画面名は、実際のFabricポータルで操作を確認したものではなく一般的な知識をもとに記載しています。Fabricの機能・UIは頻繁に更新されるため、実施前に必ず最新のFabricポータル・公式ドキュメント（`docs/REFERENCES.md`）で確認してください。

## 0. SwitchBotPlugReadings テーブル（Plug Miniの毎周期テレメトリ）

`DeviceEvents`（状態変化イベントのみ）とは別に、Plug Mini（JP）クラスの機器から**ポーリング周期ごとに（状態変化の有無に関わらず）**電力テレメトリを取り込む専用テーブルです。実装は `EventhousePlugMiniReadingStreamPublisher`（`src/MimamoriTai.Infrastructure/Fabric/EventhousePlugMiniReadingStreamPublisher.cs`）、Azure SQL側の正データは `PlugMiniReading` エンティティ（`Core/Domain/Entities.cs`）です。`DeviceEvents` 用のストリーミング取り込みロジックとは意図的にコードを共有せず、片方の障害・設定ミスがもう片方に波及しないようにしています。

- **データベース**: `MimamoriEventhouse`（`DeviceEvents` と同じKQLデータベース、`Eventhouse:DatabaseName`）
- **テーブル名**: `SwitchBotPlugReadings`（`Eventhouse:PlugMiniTableName`、既定値）
- **マッピング名**: `SwitchBotPlugReadingsMapping`（`Eventhouse:PlugMiniMappingName`、既定値。JSON取り込み用マッピングオブジェクトはEventhouse側で別途作成が必要 — 本ドキュメントはコード内の設定キーのみを記載し、実際のマッピング/テーブル作成は行っていません）
- **列一覧**（`PlugMiniReadingRecord`、`Core/Abstractions/IPlugMiniReadingStreamPublisher.cs` 参照）:

  | 列名 | 型 | 説明 |
  |---|---|---|
  | `readingId` | guid | 一意なレコードID（`PlugMiniReading.Id`） |
  | `householdId` | guid | 世帯ID |
  | `deviceId` | guid | 機器ID |
  | `deviceName` | string | 機器名（取り込み時点のスナップショット） |
  | `room` | string | 部屋名（取り込み時点のスナップショット） |
  | `voltageV` | real (nullable) | 電圧（V）。SwitchBot Plug Mini (JP) ステータスの `voltage` フィールドをそのまま使用 |
  | `currentMa` | real (nullable) | 電流（mA）。ステータスの `electricCurrent` フィールド |
  | `dailyEnergyWh` | real (nullable) | **注意（仕様の曖昧さ）**: SwitchBot公式ドキュメントのPlug Mini (JP) ステータスにある `weight` フィールドを直接この列にマッピングしています。既存コードのコメント（`SwitchBotDeviceProvider.cs`）は「1日の消費電力量」としていますが、公式仕様は単位を明示的に確定できていません（Wh・W・その他の可能性）。本実装では **Wh（ワット時）として扱う判断をしました**が、これは実測値と突き合わせて検証していない仮定です。実機での検証が済むまで、この列の値は目安としてのみ扱ってください（TODO: 実機データでの単位検証）。 |
  | `usageMinutesToday` | int (nullable) | 当日の稼働時間（分）。ステータスの `electricityOfDay` フィールド（`StatusBody` DTOに今回追加）。SwitchBot公式ドキュメントでは「今日の使用時間（分）」とされているフィールドです。 |
  | `approxWatts` | real (nullable) | **近似値**: `voltageV * currentMa / 1000`（力率1と仮定した概算のみ）。`voltageV`/`currentMa` の両方が存在する場合のみ計算され、片方でも欠けている場合は null です。 |
  | `occurredAtUtc` | datetime | ポーリング周期の時刻（UTC、ISO 8601） |

- **重複排除キー**: Azure SQL側では `(HouseholdId, DeviceId, OccurredAtUtc)` の組をアプリケーションレベルの重複排除キーとして使用しています（同じポーリング周期内で同じ機器の読み取りを二重挿入しない。詳細は `SwitchBotPollingCycleService` とそのテスト参照）。Eventhouse側にはこの一意性を強制するインデックス/ポリシーは構築していません（KQLの性質上、重複投入されても分析クエリ側で `summarize arg_max(...)` 等で最新値のみを扱うか、`OccurredAtUtc` での重複排除を行うことを推奨します）。
- **公開タイミング**: `PlugMiniReading.PublishedToStreamAtUtc` が null の行のみを対象に、`PlugMiniReadingPublishService`（`Core/Application/PlugMiniReadingPublishService.cs`）が `DeviceEvent`/`EventStreamPublishService` と全く同じ「バッチ公開 → 成功した行だけタイムスタンプを刻む」パターンでバックグラウンド公開します（`PlugMiniReadingPublishBackgroundService`）。Fabricへの送信が失敗しても例外は投げず、次回のバックグラウンド実行で再送されます。
- **未実施の作業（人間が行う必要あり）**: 上記テーブル・マッピングオブジェクトの実際のEventhouse側での作成、および `Eventhouse:PlugMiniTableName`/`PlugMiniMappingName` に対応する取り込みロールの権限確認は、本セッションでは一切行っていません（Fabric側リソース作成はスコープ外のため）。

## 1. ワークスペースの作成
2. 左下の「ワークスペース」→「新しいワークスペースの作成」から、見守り隊専用のワークスペースを作成します（Fabric容量が必要）。

## 2. SQLデータのミラーリング／取り込み

本アプリのデータは SQL Server（またはSQLite）に保存されています。Fabricから参照可能にする方法は主に2通りです。

- **Azure SQL Database のミラーリング**: SQL Server が Azure SQL Database の場合、Fabricの「Mirrored Azure SQL Database」機能でほぼリアルタイムにOneLakeへ複製できます。
- **Data Pipeline / Dataflow による定期取り込み**: オンプレミスSQL Server や SQLite ファイルの場合は、Fabric Data Pipeline や Dataflow Gen2 でLakehouse/Warehouseへ定期コピーします。

対象テーブルの例: `DeviceEvents`, `DailyActivitySummaries`（未実装のためビューで代用可）, `Devices`, `People`, `RiskAssessments`。

### デモ／LINEアカウント別の表示切替

移行 `AddAnalyticsProfileViews` は、Power BIまたはFabric Real-Time Intelligenceで同じプルダウンを作れるように次のSQLビューを用意します。

- `mimamori.vw_AnalyticsProfiles`: 表示対象ディメンション。`AnalyticsProfileName` は `デモデータ`、`まさあき（LINE）`、`わが家（LINE未連携）` のような表示名です。
- `mimamori.vw_CurrentDeviceStatus`
- `mimamori.vw_DailyActivity`
- `mimamori.vw_RecentDeviceActivity`
- `mimamori.vw_PlugMiniReadings`

各ファクトビューには `AnalyticsProfileId`、`AnalyticsProfileName`、`DataScope`（`Demo` / `LineAccount`）が含まれます。Power BIでは `vw_AnalyticsProfiles[AnalyticsProfileId]` から各ファクトビューの同名列へ1対多のリレーションを作り、`AnalyticsProfileName` をスライサーに配置します。`DataScope` を使えば「デモ／実データ」だけの切替もできます。

LINEの生の `userId` は分析ビューに公開しません。1世帯に複数のLINE受信者がいる場合は、最後に利用した有効な受信者をその世帯の表示プロフィールとして採用します。センサーデータ自体はLINE受信者ではなく世帯に属するため、選択後の全ビジュアルは同じ `HouseholdId` のデータへ絞り込まれます。Webダッシュボード上部の「表示データ」プルダウンも同じ規則を使用します。

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

実装が完了したら、次の設定を行ってください（プレースホルダーのみ・実際の値は絶対にコミットしないこと。ただしワークスペースID/Data AgentIDはシークレットではないため上記「現状の実装状況」に実値を記載済みです）。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Fabric:WorkspaceId" "<your-fabric-workspace-id>"
dotnet user-secrets set "Fabric:DataAgentId" "<your-fabric-data-agent-id>"
dotnet user-secrets set "Fabric:McpUrl" "<your-fabric-data-agent-mcp-url>"
```

`Fabric:Enabled` を `true` にするのを忘れないでください（`appsettings.Development.json` または環境変数 `Fabric__Enabled=true`）。
