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
