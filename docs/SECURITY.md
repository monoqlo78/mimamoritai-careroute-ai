# セキュリティ方針

## 秘密情報の取り扱い

- **すべての秘密情報（APIキー、トークン、接続文字列）は `dotnet user-secrets` またはホスティング環境の環境変数／シークレットストアからのみ供給します。**
- **本番（Azure App Service）では Azure Key Vault を唯一のシークレット供給元とし、App Service のアプリケーション設定に秘密情報を1件も置きません。**（下記「本番のシークレット供給（Key Vault + マネージドID）」参照）
- `src/MimamoriTai.Web/appsettings.json` および `appsettings.Development.json` には、対象キー（`ConnectionStrings:AppDb`, `OrcaRouter:ApiKey`, `Line:ChannelAccessToken`, `Line:ChannelSecret`, `Line:AlertToId`, `SwitchBot:Token`, `SwitchBot:Secret`, `Fabric:WorkspaceId`, `Fabric:DataAgentId`, `Fabric:McpUrl`）は**空文字列のプレースホルダーとしてのみ**存在し、実際の値をコミットしてはいけません。
- 各オプションクラス（`OrcaRouterOptions`, `SwitchBotOptions`, `LineOptions`, `FabricOptions`）は `IsConfigured` プロパティを持ち、必須項目が埋まっていない場合は自動的にモック実装へフォールバックします（`ServiceCollectionExtensions.cs`）。これにより、秘密情報が無い状態で誤って実サービスへ接続しようとすることを防いでいます。
- Microsoft Fabricの認証は、コード内に静的なシークレットを置かず `Azure.Identity`（`DefaultAzureCredential`）を利用します（`docs/FABRIC_SETUP.md` 参照）。同じマネージドIDで Key Vault からシークレットを読み出します。
- **重要: ローカル開発の User Secrets（`SwitchBot:Token`/`SwitchBot:Secret` など）は本番環境へ一切転記・移行しません。** 本番では各世帯のオーナーが自分のSwitchBot Token/Secretを「LINE連携設定」画面（`/settings/switchbot`）から個別に入力し、世帯ごとに暗号化して保存します（下記「世帯ごとのSwitchBot認証情報」参照）。User Secretsのグローバル `SwitchBot:Token`/`Secret` はローカル開発のブートストラップ専用の経路として残していますが、`SwitchBot:AllowGlobalFallback=true` を明示しない限り本番相当の設定では使われません。

## 本番のシークレット供給（Key Vault + マネージドID）

本番の App Service には、秘密情報を1件も置きません。アプリは起動時に Azure Key Vault を構成プロバイダーとして追加し、そこからすべての秘密情報を読み込みます。

- **実装**: `AddMimamoriTaiKeyVault()`（`src/MimamoriTai.Web/Services/KeyVaultConfigurationExtensions.cs`）を `Program.cs` の先頭で呼び出します。`KeyVault:Uri` が設定されているときだけ `DefaultAzureCredential` で `SecretClient` を作り、`builder.Configuration.AddAzureKeyVault()` で構成に重ねます。
- **認証はパスワードレス**: App Service のシステム割り当てマネージドIDに Key Vault の `Key Vault Secrets User` ロールを付与しています。**Key Vault へ接続するための資格情報そのものが存在しません。** ローカル開発では同じ `DefaultAzureCredential` が開発者のログイン（Azure CLI / Visual Studio）を拾います。
- **ゼロコンフィグは維持**: `KeyVault:Uri` が空（`appsettings.json` の既定）ならプロバイダーは追加されず、何も起きません。`git clone` して `dotnet run` するだけでモック実装で全機能が動く、という前提は変わりません。
- **シークレット名の変換規則**: Key Vault のシークレット名にはコロンを使えないため、既定の `KeyVaultSecretManager` は **`--` を構成階層の `:` に読み替えます**。つまり `OrcaRouter:ApiKey` に対応するシークレット名は **`OrcaRouter--ApiKey`** です（App Service のアプリ設定で使う `__` とは別の記法なので注意）。
- **反映**: `ReloadInterval` は30分です。シークレットをローテーションしても、再デプロイなしで最大30分後に反映されます。
- **監査**: Key Vault 側にアクセスログが残るため、「どのIDがいつどのシークレットを読んだか」を後から追跡できます。アプリ設定に平文で置く方式では得られない性質です。


## 世帯ごとのSwitchBot認証情報（Data Protectionによる暗号化）

各世帯のSwitchBot Token/Secretは、`SwitchBotConnection` エンティティ（`Core/Domain/Entities.cs`）に **平文では一切保存されません**。保存される列は `EncryptedToken`/`EncryptedSecret`（保護済みブロブ文字列）のみです。

- **暗号化の実装**: `ICredentialProtector`（`Core/Abstractions/ICredentialProtector.cs`）の唯一の本番実装 `DataProtectionCredentialProtector`（`Infrastructure/Security/DataProtectionCredentialProtector.cs`）が、ASP.NET Core Data Protection（`IDataProtectionProvider.CreateProtector(purpose)`）をラップします。
- **purpose文字列**: `"MimamoriTai.SwitchBotCredentials.v1"` に固定しています。**この文字列は将来にわたって変更してはいけません** — 変更すると、既存の全 `SwitchBotConnection` 行が復号不能になります。将来キーローテーションが必要な場合は、新しいpurposeで再暗号化しつつ旧purposeでも読めるようにする移行手順を別途設計してください（単純なリネームでは対応できません）。
- **独自の可逆暗号（XOR/Base64等）は一切使用しません。** Data Protectionのみを使う方針です。
- **キーリングの永続化**:
  - ローカル開発（`IsDevelopment()`）: ASP.NET Coreの既定のローカルキーリング（ユーザープロファイル配下）をそのまま使います。追加設定は不要です。
  - **本番（非Development環境）は、`DataProtection:KeyDirectory` に永続化された（アプリの再起動・再デプロイをまたいで消えない）ディレクトリパスを必ず設定する必要があります**（例: Azure App Serviceにマウントした Azure Files 共有、または永続ボリューム）。`PersistKeysToFileSystem` でこのパスに保存されます（`ServiceCollectionExtensions.cs`）。
  - **フェイルファスト**: `Program.cs` は、非Development環境で `DataProtection:KeyDirectory` が未設定の場合、**起動時に例外を投げてプロセスを終了します**（一時的なキーリングのまま黙って起動し、再起動のたびに全世帯の暗号化済み認証情報が読めなくなる、という事故を防ぐため）。エラーメッセージにはトークン等の秘密情報は一切含みません。
- **`IHouseholdSwitchBotClientFactory`**（`Infrastructure/Devices/HouseholdSwitchBotClientFactory.cs`）が、世帯IDを受け取り、短命なスコープ内でのみ復号したToken/Secretを使って `ISwitchBotClient` を生成します。復号済みの値がスコープを超えてキャッシュされることはなく、ある世帯の復号済み認証情報が別の世帯の処理に混入することもありません（`HouseholdSwitchBotClientFactoryTests.GetClientAsync_NeverLeaksOneHouseholdsCredentialsIntoAnothers` で回帰テスト済み）。
- **解決の優先順位**: ① 世帯ごとに保存された `SwitchBotConnection`（あれば必ずこれを使う） → ②（`SwitchBot:AllowGlobalFallback=true` の場合のみ）グローバルな `SwitchBotOptions`（User Secrets由来、ローカル開発のブートストラップ専用） → ③ どちらも無ければ未設定として扱う（例外を投げない）。
- **接続の検証**: 「LINE連携設定」画面でToken/Secretを保存する前に、必ず実際に `GET /v1.1/devices` を呼び出して疎通確認します（`SwitchBotConnectionService.ValidateAndSaveAsync`）。失敗した場合は保存されず、`LastErrorMessage` にはSwitchBotのAPIから返ったエラーの種類だけを記録し、**Token/Secretの値そのものは決して記録しません**。
- **画面表示**: 保存済みのTokenやSecretがUIに再表示されることは一切ありません。画面には「未設定/接続済み/エラー」のステータスと、最終検証・最終同期日時のみを表示します（`SwitchBotConnection.EncryptedToken`/`EncryptedSecret` はUIのレスポンスにも含まれません）。

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

## LINE送信元と世帯の紐付け（連携コード）のセキュリティ

以前は、署名検証さえ通れば**すべての**LINE Webhookイベントが無条件に「デフォルト世帯」に結びつけられていました。これは単一世帯のデモでは問題になりませんが、複数世帯が本番運用された場合、ある世帯宛のはずのイベントが別世帯として処理される・すべての新規友だち追加が同じ世帯に混入するといった重大なリスクになります。

- **既定の安全側動作（`Line:AllowDefaultHouseholdFallback=false`）**: 送信元（userId/groupId）に対応する有効な `LineRecipient` 行が無い限り、**いかなる世帯にも自動的に結びつけません**。未リンクの送信元には、6桁の連携コード（`連携 123456`）を使うよう案内する返信のみを送ります。
- **連携コードの発行はログイン済みの世帯オーナーのみ可能**（`LineLinkCodeService.IsOwnerAsync` によるチェック、匿名・非オーナーは拒否）。
- **コードは平文で保存されません**（`LineLinkCode.CodeHash`、詳細は `docs/LINE_SETUP.md` 「6. 世帯とLINEを紐付ける」参照）。有効期限10分・使い捨て・試行回数制限（既定5回）により、コードの総当たりや漏洩後の悪用を防ぎます。
- **ローカルデモ限定のフォールバック**: `appsettings.Development.json` でのみ `Line:AllowDefaultHouseholdFallback=true` にしており、これは既存の単一世帯デモ体験を壊さないための限定的な例外です。本番相当の設定ファイルではこのフラグを `true` にしないでください。

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
  - データベースへのアクセスは、世帯の家族と本人のみに限定する。認証・認可の土台自体はOIDC（`Auth:Enabled`、`docs/ARCHITECTURE.md`「実認証の実装」参照）と `HouseholdAccessService` により実装済みですが、既定では `Auth:Enabled=false`（匿名デモモード）です。**SwitchBot認証情報・LINE連携コードを扱う「LINE連携設定」画面（`/settings/switchbot`）は、世帯オーナーとしてサインインしていることを必須とします**（`SwitchBotConnectionEndpoints.RequireOwnerAsync`）が、本番運用としてダッシュボード全体を保護するには `Auth:Enabled=true` とし、本番相当の認証プロバイダー（Entra External ID等）を設定する必要があります（要確認: 実運用前に必ず有効化すること）。
  - デモデータ（`DemoDataSeeder` が生成する `EventSource.Seed` 由来のデータ、`demo-` プレフィックス付きの `ExternalDeviceId`）と実データを明確に区別し、デモ環境と本番環境のデータベースを分離する。
  - LINEなど外部サービスに送信するメッセージには、必要以上の医療的・断定的な表現を含めない（`LocalDataQuestionService` の応答文言もこの方針に沿っている）。
  - Fabric Data Agentの指示文（`docs/FABRIC_SETUP.md`）にも、断定的な診断をしないよう明記している。
- 本リポジトリ・ドキュメントの範囲では、認証・認可・データ保持期間・削除ポリシー等の詳細な運用ポリシーは策定されていません。実運用移行時には別途整備が必要です（要確認）。
