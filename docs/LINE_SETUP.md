# LINE Messaging API セットアップ

見守り隊は LINE を家族連絡チャネルとして使います。実際のLINEチャネルが無くても、ダッシュボードの「LINE シミュレーター」でグループLINEでの会話を再現できるので、ハッカソンのデモには必須ではありません。実連携をしたい場合は以下の手順に従ってください。

## 1. LINE Developers でプロバイダーを作成

1. [LINE Developers Console](https://developers.line.biz/console/) にログインします（LINEアカウントが必要）。
2. 「新規プロバイダー作成」から、プロバイダー名（例: 「見守り隊」）を入力して作成します。

## 2. Messaging API チャネルを作成

1. 作成したプロバイダーの中で「新規チャネル作成」→「Messaging API」を選択します。
2. チャネル名・チャネル説明・業種などを入力し、利用規約に同意して作成します。

## 3. チャネルシークレットとチャネルアクセストークンの取得

1. 作成したチャネルの「Basic settings」タブから **Channel secret** をコピーします → `Line:ChannelSecret`
2. 「Messaging API」タブの「Channel access token」欄で **Issue** をクリックしてトークンを発行しコピーします → `Line:ChannelAccessToken`

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Line:ChannelAccessToken" "<your-line-channel-access-token>"
dotnet user-secrets set "Line:ChannelSecret" "<your-line-channel-secret>"
```

`Line:Enabled` を `true` にするのを忘れないでください（`appsettings.Development.json` または環境変数 `Line__Enabled=true`）。これが `false`（既定）または `ChannelAccessToken`/`ChannelSecret` が空の場合、アプリは自動的に `MockLineMessagingClient` にフォールバックします。

## 4. Webhook URLの設定

1. 「Messaging API」タブの **Webhook settings** に、公開されているアプリのURLを入力します:
   ```
   https://<host>/webhooks/line
   ```
   （ローカル開発では [devtunnel](https://learn.microsoft.com/azure/developer/dev-tunnels/overview) や ngrok 等でHTTPSトンネルを張る必要があります。）
2. **Use webhook** を有効化します。
3. **Verify** ボタンで疎通確認します（アプリが起動している必要があります）。

## 5. 自動応答をオフにする

LINE公式アカウントのデフォルト応答機能が有効だと、Bot本体の応答と重複してしまいます。「Messaging API」タブの下部にある **LINE Official Account features** から「LINE Official Account Manager」を開き、以下を設定してください。

- **応答メッセージ（Greeting messages 等）**: OFF
- **Webhook**: ON
- **自動応答メッセージ**: OFF

## 6. Botをグループに追加する

1. チャネルのQRコードまたはBot Basic ID（`@`から始まるID）を使い、家族のLINEグループにBotを招待します。
2. グループ内でメンションまたは通常メッセージを送ると、`/webhooks/line` にイベントが届き、`AssistantOrchestrator` を経由して応答が返ります。

## 署名検証について

Webhookエンドポイント（`WebhookEndpoints.MapWebhookEndpoints`）は、リクエストボディ全体に対して channel secret を鍵とした HMAC-SHA256 を計算し、`X-Line-Signature` ヘッダーの値と `CryptographicOperations.FixedTimeEquals` で比較検証します（`LineSignature.Verify`）。署名が一致しない、またはチャネルシークレット未設定の場合は **401 Unauthorized** を返し、メッセージは一切処理されません。

## 認証情報なしでデモしたい場合

ダッシュボード（`Home.razor`）の「LINE シミュレーター」セクションから、任意の家族メンバーとしてメッセージを送信できます。これは `MockLineMessagingClient` を通じて `AssistantOrchestrator` を呼び出すため、実際のLINE Webhookと同じ処理経路（意図解析→安全ガードレール→応答生成）を体験できます。

## 見守りアラート（LINE Push通知）

Webhookによる「家族→LINE→アプリ」の応答経路とは別に、「異常を検知したらアプリからLINEへPush通知する」経路を `WatchAlertService`（`src/MimamoriTai.Core/Application/WatchAlertService.cs`）が担います。

- **判定**: `RiskAssessmentService.Evaluate` と同じルールベースのロジックで、本人（`PersonRole.Resident`）の当日リスクを評価します。
- **送信条件**: リスクレベルが `Line:AlertRiskThreshold`（既定 `Medium`）以上になったときのみ、`Line:AlertToId` 宛に `ILineMessagingClient.PushAsync` で通知します。
- **重複防止（必須）**: 同じ人物・同じリスクレベルのアラートは `Line:AlertCooldownHours`（既定 6 時間）以内は再送しません。送信結果（成功・失敗）は `WatchAlert` テーブルに永続化され、クールダウン判定にはこの履歴を使います。
- **未設定時の挙動**: `Line:AlertToId` が空、または LINE 自体が未設定（`Line:Enabled=false` などで `MockLineMessagingClient` が使われている）場合でも、判定自体は必ず実行され、`WatchAlert` に「送信しようとした内容」が記録されます（`Success=false`）。例外は投げません。
- **自動実行**: `MimamoriTai.Web` の `WatchAlertBackgroundService`（`IHostedService`）が `Line:AlertPollIntervalMinutes`（既定 5 分）ごとに、既定世帯（作成日時が最も古い世帯）を評価します。起動後20秒程度で最初の評価が走るため、デモでも待たずに確認できます。
- **手動実行**: `POST /api/alerts/evaluate`（ボディは省略可、または `{ "householdId": "<guid>" }`）を呼ぶと、その場で同じ判定・送信ロジックが実行され、結果がJSONで返ります。ダッシュボードの「現在の見守りステータス」カードの「見守りアラートを確認」ボタンからも同じ処理を実行できます。

### 設定キー（`Line` セクション）

```jsonc
{
  "Line": {
    "AlertToId": "",              // Push先のLINEグループID／ユーザーID（空 = 未設定。実際の値はコミットしない）
    "AlertRiskThreshold": "Medium", // "Low" | "Medium" | "High"
    "AlertCooldownHours": 6,
    "AlertPollIntervalMinutes": 5
  }
}
```

`AlertToId` は他の秘密情報と同様、`dotnet user-secrets set "Line:AlertToId" "<group-or-user-id>"` またはホスティング環境の環境変数で設定してください。`appsettings.json` には空文字列のプレースホルダーのみを置きます。
