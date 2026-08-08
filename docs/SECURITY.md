# セキュリティ方針

## 秘密情報の取り扱い

- **すべての秘密情報（APIキー、トークン、接続文字列）は `dotnet user-secrets` またはホスティング環境の環境変数／シークレットストアからのみ供給します。**
- `src/MimamoriTai.Web/appsettings.json` および `appsettings.Development.json` には、対象キー（`ConnectionStrings:AppDb`, `OrcaRouter:ApiKey`, `Line:ChannelAccessToken`, `Line:ChannelSecret`, `SwitchBot:Token`, `SwitchBot:Secret`, `Fabric:WorkspaceId`, `Fabric:DataAgentId`, `Fabric:McpUrl`）は**空文字列のプレースホルダーとしてのみ**存在し、実際の値をコミットしてはいけません。
- 各オプションクラス（`OrcaRouterOptions`, `SwitchBotOptions`, `LineOptions`, `FabricOptions`）は `IsConfigured` プロパティを持ち、必須項目が埋まっていない場合は自動的にモック実装へフォールバックします（`ServiceCollectionExtensions.cs`）。これにより、秘密情報が無い状態で誤って実サービスへ接続しようとすることを防いでいます。
- Microsoft Fabricの認証は、コード内に静的なシークレットを置かず `Azure.Identity`（`DefaultAzureCredential`）を利用する設計です（実装は `docs/FABRIC_SETUP.md` 参照、現状は未実装）。

## `.gitignore` による除外

リポジトリの `.gitignore`（.NET標準テンプレート相当）は、ビルド成果物（`bin/`, `obj/`）に加え、秘密情報が誤って混入しやすい以下のようなファイル／パターンを除外対象とすべきです。

- `appsettings.*.local.json` のようなローカル専用設定ファイル
- `*.db`, `*.db-shm`, `*.db-wal`（SQLiteのデモDBファイル。`mimamoritai-demo.db` はデモデータのみを含みますが、実運用相当のデータを含めた場合は特にコミットしない）
- `secrets.json` を直接プロジェクト内に置く運用は行わない（`dotnet user-secrets` はユーザープロファイル配下の別ディレクトリに保存されるため、そもそもリポジトリに含まれない）

## LINE Webhookの署名検証

`ILineMessagingClient.VerifySignature` （実装: `LineSignature.Verify`, `src/MimamoriTai.Infrastructure/Line/MockLineMessagingClient.cs`）は、以下の手順で検証します。

1. リクエストの生ボディ（rawBody）に対して、チャネルシークレットを鍵とした HMAC-SHA256 を計算する。
2. `X-Line-Signature` ヘッダーの値をBase64デコードする。
3. 計算結果とヘッダー値を `CryptographicOperations.FixedTimeEquals`（**タイミング攻撃に耐性のある定数時間比較**）で比較する。
4. チャネルシークレットが未設定、またはヘッダーが欠落・不正な場合は **必ず `false`** を返す（＝安全側に倒す）。

署名検証に失敗したリクエストは `WebhookEndpoints.MapWebhookEndpoints` で **401 Unauthorized** を返し、`AssistantOrchestrator` には一切渡されません。

## AI家電操作のガードレール

自然言語による家電操作は `DeviceSafetyPolicy.Validate` によって多層的に制限されています（詳細は `docs/ARCHITECTURE.md` の「安全ガードレールのフロー」参照）。要点:

- **危険な家電（Heater/Kettle/Microwave/CookingDevice/Plug/MotionSensor/ContactSensor/Unknown = `SafetyClass.Restricted`）は、AIからのTurnOn/Toggle操作を一切許可しない（ただしTurnOff＝消す操作は安全側のため許可される非対称なガードレール）。**
- 機器ごとの `RemoteControlAllowed` フラグで個別に遠隔操作を無効化できる。
- LLMが返す確信度 (`confidence`) が `IntentParser.MinimumConfidence`（0.85）未満なら操作を実行しない。
- 機器名（エイリアス）が一意に特定できない場合は、機器を推測せずに確認を求める。
- LLMの出力（JSON）が不正な場合、`IntentParser.TryParse` は `null` を返し、`AssistantOrchestrator` は1回だけ修復を試みてそれでも失敗すれば何も実行しない。

## 監査ログ (`DeviceCommand`)

すべての家電操作の**試行**（成功・失敗・拒否のいずれも）は `DeviceCommand` エンティティとして永続化されます（`DeviceControlService.ExecuteAsync` / `RejectAsync`）。記録される情報:

- `OriginalText`: ユーザーの元の発言
- `Action`: 要求された操作（TurnOn/TurnOff/Toggle/GetStatus）
- `Status`: `Pending`/`Succeeded`/`Failed`/`Rejected`
- `FailureReason`: 拒否・失敗の理由（日本語の説明文）
- `AiResolvedModel`: 判定に使われたAIモデル名（`AiRequestLog` とも紐付く）
- `RequestedAtUtc` / `ExecutedAtUtc`: リクエスト時刻・実行時刻（UTC）
- `Source`: `Web`/`Line`/`System` のいずれの経路から来たか
- `RequestedByPersonId`: 要求した人物（判明する場合）

これにより、「誰が・いつ・何を・どのような理由で」操作しようとしたか（拒否も含む）を後から追跡できます。

## プライバシー上の考慮事項（高齢者見守りデータ）

- 本アプリが収集するのは**家電のON/OFFイベントとタイムスタンプのみ**であり、映像・音声・位置情報等のより機微なデータは扱いません。
- それでも、生活リズム（起床・就寝・外出の推定等）は個人の生活パターンを推測できる情報であるため、以下を推奨します。
  - データベースへのアクセスは、世帯の家族と本人のみに限定する（アプリ自体には現状、認証・認可の仕組みは実装されていません — 要確認: 本番運用前に認証層の追加が必要）。
  - デモデータ（`DemoDataSeeder` が生成する `EventSource.Seed` 由来のデータ、`demo-` プレフィックス付きの `ExternalDeviceId`）と実データを明確に区別し、デモ環境と本番環境のデータベースを分離する。
  - LINEなど外部サービスに送信するメッセージには、必要以上の医療的・断定的な表現を含めない（`LocalDataQuestionService` の応答文言もこの方針に沿っている）。
  - Fabric Data Agentの指示文（`docs/FABRIC_SETUP.md`）にも、断定的な診断をしないよう明記している。
- 本リポジトリ・ドキュメントの範囲では、認証・認可・データ保持期間・削除ポリシー等の詳細な運用ポリシーは策定されていません。実運用移行時には別途整備が必要です（要確認）。
