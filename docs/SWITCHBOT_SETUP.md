# SwitchBot API セットアップ ＆ 実装状況

## 1. SwitchBotアプリでToken/Secretを取得する

1. スマートフォンの **SwitchBot アプリ** を開きます。
2. 「プロフィール」タブ →「設定」（Preferences）を開きます。
3. 「App Version」の項目を **10回連続でタップ** します（開発者向け画面が有効化されます）。
4. 表示された「Developer Options（開発者向けオプション）」を開き、以下を取得します。
   - **Token** → `SwitchBot:Token`
   - **Secret** → `SwitchBot:Secret`

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "SwitchBot:Token" "<your-switchbot-token>"
dotnet user-secrets set "SwitchBot:Secret" "<your-switchbot-secret>"
```

`SwitchBot:Enabled` を `true` にすることも忘れないでください（`appsettings.Development.json` または環境変数 `SwitchBot__Enabled=true`）。

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

`ISwitchBotClient`（`SwitchBotClient`）は、認証ヘッダーの付与とHTTP送受信のみを実装しており、以下の3メソッドは **生のJSON文字列をそのまま返します**（レスポンスDTOへのマッピングは行いません）。

- `GetDeviceListRawAsync()` — `GET /v1.1/devices`
- `GetDeviceStatusRawAsync(deviceId)` — `GET /v1.1/devices/{deviceId}/status`
- `SendCommandRawAsync(deviceId, command, parameter, commandType)` — `POST /v1.1/devices/{deviceId}/commands`

## ⚠️ 未実装のTODO: レスポンスのDTOマッピング

`src/MimamoriTai.Infrastructure/Devices/SwitchBotDeviceProvider.cs` は `IDeviceProvider` の実装クラスとして存在しますが、**実機で公式仕様を検証するまで意図的に未実装のまま**にしてあります。現在の挙動:

- `GetDevicesAsync()` → 常に空リストを返す
- `GetStatusAsync()` → 常に `null` を返す
- `TurnOnAsync()` / `TurnOffAsync()` / `ToggleAsync()` → 常に `ProviderResult.Fail(NotImplementedReason)` を返す（`NotImplementedReason` = "SwitchBot response mapping is pending verification against the official OpenAPI v1.1 specification."）
- 呼び出しごとに `ILogger` へ警告ログを出力する

`ServiceCollectionExtensions.cs` の DI 登録では、`SwitchBotOptions.IsConfigured`（`SwitchBot:Enabled=true` かつ Token/Secret 設定済み）の場合に `IDeviceProvider` を `SwitchBotDeviceProvider` へ切り替える分岐は既に実装されています。ただし上記の通りレスポンスマッピングが未実装のため、この分岐を有効にしてもすべての操作が失敗します。実機で仕様確認が完了するまでは `SwitchBot:Enabled` を `false`（既定値）のままにし、`MockDeviceProvider` を使い続けることを推奨します（`docs/ARCHITECTURE.md` の「既知の制約」参照）。

### 実機到着後にやるべきこと（`src/MimamoriTai.Infrastructure/Devices/SwitchBotDeviceProvider.cs`）

1. `SwitchBotClient.GetDeviceListRawAsync()` の実際のレスポンスJSONを取得し、公式ドキュメントと突き合わせて `body.deviceList[]` 相当の型を定義する。
2. `GetDevicesAsync()` を実装し、`ProviderDevice(ExternalDeviceId, Name, DeviceType, Room)` へマッピングする。`DeviceType` はSwitchBot側の `deviceType` 文字列（例: `"Bot"`, `"Plug"`, `"Color Bulb"` 等）から `MimamoriTai.Core.Domain.DeviceType` への変換テーブルを新設する必要がある。
3. `GetDeviceStatusRawAsync()` のレスポンス（`body.power` などの実際のフィールド名は要確認）から `ProviderDeviceStatus(State, PowerWatts, ObservedAtUtc)` を組み立てるよう `GetStatusAsync()` を実装する。
4. `SendCommandRawAsync()` を使い `TurnOnAsync`/`TurnOffAsync`/`ToggleAsync` を実装する。SwitchBotの `command`（例: `"turnOn"`, `"turnOff"`）と `commandType`（`"command"` 等）の正確な値は機種によって異なるため、公式ドキュメントで対象機種（プラグ／ボット等）ごとに確認する。
5. レスポンスの `statusCode`（SwitchBotのAPIレベルのエラーコード）を `ProviderResult.Fail(reason)` に反映する。
6. `ServiceCollectionExtensions.cs` の `SwitchBotOptions.IsConfigured` による `IDeviceProvider` 切り替え分岐は既に実装済みなので、上記1〜5を実装したら `SwitchBot:Enabled=true` にするだけで有効化される（追加のDI変更は不要）。
7. `src/MimamoriTai.Web/Endpoints/WebhookEndpoints.cs` の `/webhooks/switchbot` エンドポイントも、現状はペイロードを読み捨てるだけのプレースホルダーなので、SwitchBot Webhookの実イベントペイロードに合わせて `DeviceEvent` を生成する処理を追加する。

## 4. デモ環境での代替

実機が無い間は `MockDeviceProvider`（`src/MimamoriTai.Infrastructure/Devices/MockDeviceProvider.cs`）がインメモリで4台の擬似デバイス（リビング照明・寝室照明・扇風機・電気ストーブ）を提供し、認証情報を一切必要としません。電気ストーブは `SafetyClass.Restricted` に分類される機器で、AIからのON操作が拒否されることを実演するために含まれています。ダッシュボードの表示・自然言語操作・安全ガードレールのデモはすべてこのモックで完結します。
