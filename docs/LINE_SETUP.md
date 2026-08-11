# LINE Messaging API セットアップ

見守り隊は LINE を家族連絡チャネルとして使います。実際のLINEチャネルが無くても、ダッシュボードの「LINE シミュレーター」でグループLINEでの会話を再現できるので、ハッカソンのデモには必須ではありません。実連携をしたい場合は以下の手順に従ってください。

## 0. 世帯とLINE送信先の紐付け（連携コード）について

複数世帯が同じLINE公式アカウントを友だち追加する運用を想定し、**「このLINEの送信元（userId/groupId）がどの世帯宛のお知らせか」は、世帯オーナーがWeb UIで発行する6桁の連携コードで確立します。** 詳細は下記「6. 世帯とLINEを紐付ける（連携コード）」を参照してください。これは、以前の実装にあった「すべてのLINE送信元を暗黙的に1つのデフォルト世帯に結びつける」という挙動（複数世帯運用では誤送信の原因になる）を置き換えるものです。

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
- **送信条件**: リスクレベルが `Line:AlertRiskThreshold`（既定 `Medium`）以上になったときのみ、その世帯に紐付く `LineRecipient`（下記「6. 世帯とLINEを紐付ける（連携コード）」で登録されたもの。未設定・グローバル互換の `Line:AlertToId` がある場合はそれも送信先に含まれます）宛に `ILineMessagingClient.PushAsync` で通知します。
- **重複防止（必須）**: 同じ人物・同じリスクレベルのアラートは `Line:AlertCooldownHours`（既定 6 時間）以内は再送しません。送信結果（成功・失敗）は `WatchAlert` テーブルに永続化され、クールダウン判定にはこの履歴を使います。
- **未設定時の挙動**: 送信先が1件も無い（世帯にLINE連携済みの `LineRecipient` が無く、`Line:AlertToId` も空）場合や LINE 自体が未設定（`Line:Enabled=false` などで `MockLineMessagingClient` が使われている）場合でも、判定自体は必ず実行され、`WatchAlert` に「送信しようとした内容」が記録されます（`Success=false`）。例外は投げません。
- **自動実行**: `MimamoriTai.Web` の `WatchAlertBackgroundService`（`IHostedService`）が `Line:AlertPollIntervalMinutes`（既定 5 分）ごとに、既定世帯（作成日時が最も古い世帯）を評価します。起動後20秒程度で最初の評価が走るため、デモでも待たずに確認できます。
- **手動実行**: `POST /api/alerts/evaluate`（ボディは省略可、または `{ "householdId": "<guid>" }`）を呼ぶと、その場で同じ判定・送信ロジックが実行され、結果がJSONで返ります。ダッシュボードの「現在の見守りステータス」カードの「見守りアラートを確認」ボタンからも同じ処理を実行できます。

### 設定キー（`Line` セクション）

```jsonc
{
  "Line": {
    "AlertToId": "",              // （後方互換のグローバル送信先。空 = 未設定。実際の値はコミットしない。通常は下記の連携コード経由の LineRecipient を使うため空のままで構いません）
    "AlertRiskThreshold": "Medium", // "Low" | "Medium" | "High"
    "AlertCooldownHours": 6,
    "AlertPollIntervalMinutes": 5,
    "AllowDefaultHouseholdFallback": false // 下記「6.」参照。本番では必ず false のままにしてください
  }
}
```

`AlertToId` は他の秘密情報と同様、`dotnet user-secrets set "Line:AlertToId" "<group-or-user-id>"` またはホスティング環境の環境変数で設定してください。`appsettings.json` には空文字列のプレースホルダーのみを置きます。

## 6. 世帯とLINEを紐付ける（連携コード）

### 背景・解決した問題

以前の実装では、LINE Webhookに届いたイベント（`follow`/`unfollow`/`message`/`postback` すべて）を、送信元（userId/groupId）に関係なく一律 `HouseholdAccessService.ResolveDefaultAsync`（作成日時が最も古い世帯）に結びつけていました。これは単一世帯のデモには十分ですが、複数世帯が本番運用された場合、**すべてのLINE送信元が同じ「デフォルト世帯」に誤って結びつく**という重大なバグでした。

現在の実装（`WebhookEndpoints.MapWebhookEndpoints` / `LineLinkCodeService`）は、この問題を「連携コード」方式で解決しています。

### 使い方（世帯オーナー向け）

1. ログイン後、「LINE連携設定」画面（`/settings/switchbot` の下部セクション、または独立した設定ページ）を開きます。
2. 「連携コードを発行する」ボタンを押すと、6桁の数字コード（例: `482913`）が画面に表示されます（**このコードは10分間のみ有効で、一度使うと失効します。再表示はできません**）。
3. 見守り隊の公式LINEアカウントを友だち追加した状態で、トーク画面に `連携 482913` のように送信します（「連携」の直後に数字6桁。全角数字・前後の空白も許容）。
4. Webhookがメッセージを受信すると、コードを検証し、一致すればその送信元（userId/groupId）を世帯に紐付ける `LineRecipient` 行を作成/有効化します。以降、その世帯の見守りアラートやLINE応答はこの送信元に届きます。

### 実装の要点

- **`LineLinkCode` エンティティ**（`Core/Domain/Entities.cs`）: `HouseholdId`, `CodeHash`（HMAC-SHA256、平文コードは保存しない）, `ExpiresAtUtc`（発行から10分）, `UsedAtUtc`（使用済みなら非null、使い捨て）, `AttemptCount`（レート制限）。
- **1世帯につき常に最大1件の未使用コード**という方針です（新しく発行すると、その世帯の以前の未使用コードは即座に無効化されます）。複数のコードを並行して有効にすることはできません。
- **コードのハッシュ化**: `LineLinkCodeService` はコードそのものをDBに保存せず、固定の非シークレットな文字列をキーにしたHMAC-SHA256（`CodeHash`）のみを保存します（Data Protectionの非決定性はハッシュ照合に使えないため、あえて別方式）。実際のセキュリティは秘匿されたハッシュ鍵ではなく、**10分の有効期限・使い捨て・試行回数制限**によって担保されます（詳細は同ファイルのXMLコメント参照）。
- **試行回数制限**: 現在有効な（未使用・未失効の）コードすべてに対して、間違ったコードでの照合失敗のたびに `AttemptCount` が加算されます。`LineLinkCodeService.MaxAttempts`（既定 5）に達したコードはその場で失効させます。
- **既にLINE連携済みの送信元の優先解決（重要な修正）**: Webhookはまずその送信元（userId/groupId）に対応する**有効な** `LineRecipient` 行が既にあるかを確認し、あればその世帯を直接使います。`ResolveDefaultAsync`（デフォルト世帯へのフォールバック）は、**送信元が一切リンクされておらず、かつ `Line:AllowDefaultHouseholdFallback` が明示的に `true` の場合のみ**呼ばれます。
- **`Line:AllowDefaultHouseholdFallback`**（既定 `false`）: 本番相当の設定では必ず `false` のままにしてください。`appsettings.Development.json` でのみ `true` にしており、これは既存のローカルデモ体験（未リンクの送信元でも即座にデフォルト世帯として応答する）を壊さないためです。`true` の場合でも、既にリンク済みの送信元は常に自分の世帯を使い、デフォルト世帯へのフォールバックは「一度も連携コードを使ったことがない新規の送信元」にのみ適用されます。
- **未リンクの `follow` イベントへの応答**: 新規に見守り隊を友だち追加した送信元（まだどの世帯にも連携されていない）が `follow` イベントを送ると、Webhookは「連携コードの発行方法」を案内する日本語メッセージを自動返信します（`WebhookEndpoints.cs` の案内文言を参照）。
- **LINE Login経由の自動連携**: 現時点では未実装です（TODO）。`AppUser.LineUserId`（LINE LoginのOIDC `sub`）と、Messaging APIのWebhookから届く `userId` が同一チャネル/プロバイダー内で一致することが確認できれば自動連携も可能ですが、チャネル/プロバイダーの同一性を安全に確認できる確証が持てなかったため、今回は連携コード方式のみを実装し、これは明示的なTODOとして残しています。連携コード方式は、LINE Login経由の自動連携が将来実装された場合でも、確実なフォールバック手段として維持される想定です。

### 世帯間でのLINEユーザーID再割り当てについて

`LineRecipient` は `(HouseholdId, LineUserId)` の組でユニークですが、同じ `LineUserId` が複数の世帯で同時に「有効（`IsActive=true`）」であることは想定していません。連携コードの償還時（`LineLinkCodeService.RedeemCodeAsync`）に、その `LineUserId` が別の世帯で保持していた有効な行があれば自動的に無効化（`IsActive=false`）してから、新しい世帯の行を有効化します。これにより「1つのLINEアカウントは常にちょうど1つの世帯からのお知らせを受け取る」という状態を保ちます（意図的な再連携は許可、事故的な多重紐付けは防止）。

