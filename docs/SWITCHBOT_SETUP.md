# SwitchBot API セットアップ ＆ 実装状況

## 0. 本番運用: 世帯ごとにWeb UIから接続する（推奨）

**本番環境では、Token/SecretはApp Serviceの設定や `dotnet user-secrets` には入れません。** 各世帯のオーナーが、ログイン後に **「LINE連携設定」画面（`/settings/switchbot`）** で自分のSwitchBot Token/Secretを直接入力します。入力された値は保存前に `GET /v1.1/devices` で疎通確認され、成功した場合のみ ASP.NET Core Data Protection で暗号化された状態で世帯ごとに（`SwitchBotConnection` テーブルへ）保存されます。平文はデータベースにもログにも一切残りません。詳細は `docs/SECURITY.md`「世帯ごとのSwitchBot認証情報」を参照してください。

以下の「1. SwitchBotアプリでToken/Secretを取得する」の手順自体は、本番でもローカル開発でも同じです（取得したToken/Secretの**入力先**が異なるだけです）。

## 1. SwitchBotアプリでToken/Secretを取得する

1. スマートフォンの **SwitchBot アプリ** を開きます。
2. 「プロフィール」タブ →「設定」（Preferences）を開きます。
3. 「App Version」の項目を **10回前後連続でタップ** します（開発者向け画面が有効化されます。タップ回数は実機・アプリバージョンによって多少前後することがあります）。
4. 表示された「Developer Options（開発者向けオプション）」を開き、以下を取得します。
   - **Token**
   - **Secret**

**本番（各世帯のオーナー）**: 上記で取得したToken/Secretを、ログイン後に開く「LINE連携設定」画面（`/settings/switchbot`）にそのまま貼り付けて保存してください。

**ローカル開発（グローバル/ブートストラップ用の唯一の経路）**: 開発時に世帯ごとの設定UIを経由せず素早く動作確認したい場合のみ、以下のUser Secretsを使えます。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "SwitchBot:Token" "<your-switchbot-token>"
dotnet user-secrets set "SwitchBot:Secret" "<your-switchbot-secret>"
```

`SwitchBot:Enabled` を `true` にすることも忘れないでください（`appsettings.Development.json` または環境変数 `SwitchBot__Enabled=true`）。ポーリング間隔（既定5分）を変えたい場合は `SwitchBot:PollIntervalMinutes`（環境変数なら `SwitchBot__PollIntervalMinutes`）を設定してください。

このグローバル設定は、**`SwitchBot:AllowGlobalFallback=true` のときのみ**、かつ対象世帯に世帯ごとの `SwitchBotConnection` が1件も保存されていないときのみフォールバックとして使われます（`HouseholdSwitchBotClientFactory` の優先順位: ① 世帯ごとの暗号化済み接続 → ②（`AllowGlobalFallback=true` の場合のみ）グローバル `SwitchBotOptions` → ③ 未設定）。既定値は `false` で、本番向けの設定（`appsettings.json` 等）では明示的に有効化しない限りこのフォールバックは効きません。`appsettings.Development.json` でのみ `true` にすることを想定しています。

## 2. v1.1 の署名方式

SwitchBot OpenAPI v1.1 は、リクエストごとに以下の値を計算してヘッダーに付与する方式で認証します（`src/MimamoriTai.Infrastructure/Devices/SwitchBotClient.cs` の `ApplyAuthHeaders` に実装済み）。

1. `t`: 現在時刻のUnixミリ秒（文字列）
2. `nonce`: ランダムなGUID（ハイフン無し文字列）
3. `payload = token + t + nonce` を作成
4. `sign = Base64( HMACSHA256(secret, payload) )` を計算
5. リクエストヘッダーに以下を設定:
   - `Authorization: <token>`
   - `sign: <sign>`
   - `t: <t>`
   - `nonce: <nonce>`

エンドポイントのベースURLは `https://api.switch-bot.com`（`SwitchBotOptions.BaseUrl`）です。

## 3. 実装済みの範囲

`ISwitchBotClient`（`SwitchBotClient`）は、認証ヘッダーの付与とHTTP送受信のみを実装しており、以下の3メソッドは **生のJSON文字列をそのまま返します**（レスポンスDTOへのマッピングは `SwitchBotDeviceProvider` が行います）。

- `GetDeviceListRawAsync()` — `GET /v1.1/devices`
- `GetDeviceStatusRawAsync(deviceId)` — `GET /v1.1/devices/{deviceId}/status`
- `SendCommandRawAsync(deviceId, command, parameter, commandType)` — `POST /v1.1/devices/{deviceId}/commands`

`SwitchBotDeviceProvider`（`src/MimamoriTai.Infrastructure/Devices/SwitchBotDeviceProvider.cs`）は上記の生JSONを、公式仕様（[OpenWonderLabs/SwitchBotAPI](https://github.com/OpenWonderLabs/SwitchBotAPI)、README.mdおよび `devices/*.md`）で確認した以下のレスポンス形状に基づいてマッピングします。

- 共通エンベロープ: `{ "statusCode": 100, "message": "success", "body": {...} }`。`statusCode` が100以外（`401 Unauthorized`、`190 System error` 等）は失敗として扱われ、例外は投げません。
- `GET /v1.1/devices` の `body` には物理デバイス `deviceList`（`deviceId`, `deviceName`, `deviceType`, `hubDeviceId` 等）と、Hub経由の赤外線リモコン `infraredRemoteList`（`deviceId`, `deviceName`, `remoteType`, `hubDeviceId`）の2つの配列があり、両方をマッピングします（高齢者宅では照明や扇風機がHub経由の赤外線リモコンであることが多いため）。
- `GET /v1.1/devices/{id}/status` の `body` は機種ごとにフィールドが異なります（例: Botの `power`（ON/OFF文字列）、Motion Sensorの `moveDetected`（真偽値）、Contact Sensorの `openState`（`open`/`close`/`timeOutNotClose`）、Plug Mini (JP) は `power` フィールドが**存在せず** `electricCurrent`（mA）と `weight`（1日の消費電力量、W）から状態を推定）。
- コマンド送信は `{"command":"turnOn"/"turnOff","parameter":"default","commandType":"command"}`。`toggle` は機種（例: Bot）によっては対応コマンドが無いため、`ToggleAsync` は現在の状態を取得してから逆の明示コマンドを送信します。

デバイス種別（SwitchBotの `deviceType`/`remoteType` 文字列）は `MimamoriTai.Core.Domain.DeviceType` にマッピングされます。マッピング表に無い機種（Hub、Curtain、Meter、Lock、Robot Vacuumなど）は `DeviceType.Unknown` にフォールバックし、`DeviceSafetyPolicy` により自動的に `Restricted`（安全側）として扱われます。

## 4. 実機データをアプリへ反映する

1. `SwitchBot:Enabled=true` とToken/Secretを設定して起動すると、`IDeviceProvider` が `SwitchBotDeviceProvider` に切り替わります。
2. ダッシュボードの「実機を同期」ボタン（または `POST /api/devices/sync`）を押すと、`DeviceSyncService` が実機の機器一覧を取得し、`Devices` テーブルへ反映します（新規は追加、既存は名前/種別/部屋を更新、実機側から消えた機器は削除せず無効化）。同期は冪等で、変化が無ければ2回目の実行は何も変更しません。
3. 同期後は `SwitchBotPollingBackgroundService` が既定5分間隔（`SwitchBot:PollIntervalMinutes`）で各機器のステータスをポーリングし、ON/OFF・人感・開閉の変化を検知したときだけ `DeviceEvent`（`Source=SwitchBotPoll`）を記録します。状態が変わらない限り重複イベントは作成されません。
4. 同期は機器を発見するだけで、遠隔操作の許可（`RemoteControlAllowed`）は自動では付与されません。安全のため、AIチャット/LINEからの操作を許可する機器は運用者が個別に設定してください。
5. `src/MimamoriTai.Web/Endpoints/WebhookEndpoints.cs` の `/webhooks/switchbot` エンドポイントは、現状はペイロードを読み捨てるだけのプレースホルダーです。SwitchBot Webhookからのリアルタイムイベント受信（ポーリングより低遅延）が必要な場合は、別途実装が必要です。

## 5. デモ環境での代替

実機が無い間は `MockDeviceProvider`（`src/MimamoriTai.Infrastructure/Devices/MockDeviceProvider.cs`）がインメモリで4台の擬似デバイス（リビング照明・寝室照明・扇風機・電気ストーブ）を提供し、認証情報を一切必要としません。電気ストーブは `SafetyClass.Restricted` に分類される機器で、AIからのON操作が拒否されることを実演するために含まれています。ダッシュボードの表示・自然言語操作・安全ガードレールのデモはすべてこのモックで完結します。
