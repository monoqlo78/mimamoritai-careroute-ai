# LINEワンタッチ通報 セットアップ

高齢のご本人が、文字入力なしでリッチメニューの1タップだけで家族に状況を伝えられるようにする機能です。この文書は、実際のLINE公式アカウントを取得したあとに行う設定手順をまとめたものです。

## 1. 専用のLINE公式アカウントが必要

**見守り隊専用の新しいLINE公式アカウント（チャネル）を作成してください。既存のSDSiGNER用アカウントを流用しないでください。** 用途（Botの応答内容、リッチメニュー、Webhook）が異なるため、共用すると通知の誤配信や設定の衝突が起きます。

専用アカウントは作成・接続済みです。

- アカウント名: `見守り隊`
- Basic ID: `@755ykcrx`
- 友だち追加URL: `https://line.me/R/ti/p/@755ykcrx`
- QRコード: `https://qr-official.line.me/gs/M_755ykcrx_GW.png`
- 本番Webhook: `https://app-mimamoritai-hack.azurewebsites.net/webhooks/line`
- LINE Developersプロバイダー: `見守り隊`（ID `2005421841`）
- Messaging APIチャネルID: `2011034584`

アカウント作成手順自体は `docs/LINE_SETUP.md` の「1. LINE Developers でプロバイダーを作成」〜「2. Messaging API チャネルを作成」と同じです（プロバイダー名・チャネル名は「見守り隊」など専用の名称にしてください）。

## 2. 認証情報の設定

チャネルの **Basic settings** から Channel secret を、**Messaging API** タブから Channel access token（長期）を発行し、以下を実行します。

```powershell
cd src/MimamoriTai.Web
dotnet user-secrets set "Line:ChannelAccessToken" "<新しいチャネルのアクセストークン>"
dotnet user-secrets set "Line:ChannelSecret" "<新しいチャネルのシークレット>"
dotnet user-secrets set "Line:Enabled" "true"
```

`Line:Enabled` が `false`（既定）、またはトークン／シークレットが空の場合、アプリは自動的に `MockLineMessagingClient` にフォールバックし、実際のLINEには何も送信されません。これらの値は `appsettings.json` に書かないでください（User Secrets または本番環境変数のみ）。

## 3. Webhook URLの設定

「Messaging API」タブの **Webhook settings** に以下を設定します。

```
https://<公開ホスト>/webhooks/line
```

- **Use webhook**: ON
- **Verify** で疎通確認（アプリ起動が必要）
- 「LINE Official Account Manager」側の応答メッセージ・自動応答メッセージは OFF にしてください（`docs/LINE_SETUP.md` の「5. 自動応答をオフにする」を参照）。

署名検証（`X-Line-Signature`、HMAC-SHA256）はアプリ側で必須になっており、`Line:ChannelSecret` が正しく設定されていないとWebhookは常に401を返します。

## 4. リッチメニューのセットアップ

ご本人のトーク画面下部に、常時表示される6つの大きなボタン（リッチメニュー）を設定します。専用アカウントのアクセストークンが発行できたら、以下のコマンドを実行してください（PowerShell 7 が必要です）。

```powershell
./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken "<チャネルアクセストークン>"
```

- 既定では、Blenderで制作した見守りフクロウのCGを組み込んだ `assets/line-rich-menu.png` を使用します。CGの編集可能な原本は `assets/line-mimamori-mascot.blend` です。
- 置換は安全な順序で行います。新しいメニューの作成、画像アップロード、デフォルト設定、設定確認がすべて成功したあとにだけ、古い `MimamoriTai-` メニューを削除します。途中で失敗しても、現在動いているメニューは残ります。
- CGを再制作する場合は、既存のBlenderプロジェクトを開いたプロセスとは分離した新規プロセスで `assets/create-mimamori-mascot.py` を実行します。その後 `assets/create-line-rich-menu.ps1` で2500×1686pxのメニュー画像を再構成します。
- `-ImagePath ''` を明示した場合は、従来の簡易画像を `scripts/generated/line-rich-menu.png` に生成できます。
- Windows以外の環境、または画像を差し替えたい場合は `-ImagePath` で独自のPNG（2500×1686px）を指定できます。寸法とファイル形式（PNGシグネチャ）はスクリプト側で検証されます。
- トークンは一切ログや画面に出力されません。実行結果として、作成されたリッチメニューID・デフォルト設定の確認結果のみが表示されます。

```powershell
# 生成画像を確認だけしたい／独自画像を使いたい場合
./scripts/setup-line-rich-menu.ps1 -ChannelAccessToken "<token>" -ImagePath ".\my-menu.png"
```

## 5. ボタンの動作

各ボタンはLINEの `postback`（一部は `message`）アクションとして送信され、`WebhookEndpoints` → `LinePostbackActionService`（`src/MimamoriTai.Core/Application/LinePostbackActionService.cs`）が処理します。

| ボタン表示 | 送信データ | 返信内容（本人へ） | 家族への通知 | 記録 |
| --- | --- | --- | --- | --- |
| 助けて | `action=emergency` | 「家族に知らせました」という趣旨のやさしい日本語（他に連絡先がない場合は119番を案内） | 送信者本人を除く、世帯内の他のアクティブなLINE連絡先全員へ高優先度のテキスト（本人の名前・日本時間のタイムスタンプ入り、医療的な断定はしない） | `FamilyMessage` として家族フィードに残る |
| 体調が悪い | `action=unwell` | 「家族に知らせます」という趣旨の返信 | 他の連絡先へ「体調が悪い」旨のやさしい通知 | `FamilyMessage` として記録 |
| 大丈夫 | `action=okay` | 「大丈夫を受け付けました」 | 送信なし（緊急性がないため） | `FamilyMessage` として記録 |
| 今日の様子 | `action=status` | Fabric Data Agentへ最大2秒で問い合わせ、応答できない場合はローカルの当日活動データから生活リズムを返信 | 送信なし | Webhookの安全な構造化ログに記録 |
| 家族に連絡 | `action=contact_family` | 「家族に連絡しました」という趣旨の返信 | 他の連絡先へ連絡依頼の通知 | `FamilyMessage` として記録 |
| メッセージ | メッセージ「相談したいです」 | 通常のテキストメッセージとして `AssistantOrchestrator` が応答 | （通常のチャット経路と同じ） | 既存の会話ログに準拠 |

「他の連絡先」は、実行時点でその世帯に登録されている、送信者自身を除くアクティブな `LineRecipient` 全員です。

## 6. ユーザー種別（本人／家族）に関する制限

現在の `LineRecipient` エンティティには「本人（ご利用者）」と「家族」を区別するロール項目がありません。そのため本機能は、安全に判定できない役割推定を行わず、代わりに「送信者本人以外の、世帯内のアクティブな連絡先すべて」へ通知する設計にしています。

- 世帯にご本人と家族が1人ずつしか登録されていない典型的な運用では、想定どおり「本人→家族へ通知」として機能します。
- 家族が複数人いる世帯では、ボタンを押した人（通常は本人）以外の全員に通知が届きます。家族の誰かがLINEグループ経由でボタンを操作した場合も、その人を除く全員に届く点に注意してください。
- 将来的にロールを追加する場合は、`LineRecipient` にロール項目を追加したうえで `LinePostbackActionService.PushToOthersAsync` の宛先解決を見直してください。

## 7. 動作確認手順

1. 上記の手順1〜4を実施し、`dotnet run --project src/MimamoriTai.Web` でアプリを起動（またはデプロイ先を稼働）させます。
2. 見守り隊専用アカウントを友だち追加し、フォローイベントの歓迎メッセージ（リッチメニューの6ボタンの説明）が届くことを確認します。
3. リッチメニューの各ボタンを実際にタップし、以下を確認します。
   - 「助けて」: 本人へやさしい確認メッセージが返り、他の家族連絡先に緊急通知が届く（家族フィード・ダッシュボードにも表示される）。
   - 「体調が悪い」「家族に連絡」: それぞれの通知が家族側に届く。
   - 「大丈夫」: 本人への返信のみで、家族へのPush通知が発生しないこと。
   - 「今日の様子」: Fabricまたはローカルフォールバックから、当日の活動に基づく様子説明が返る。
4. 家族が1人も登録されていない、または他のアクティブな連絡先がいない世帯で「助けて」を押し、119番を案内する返信になることを確認します。
5. `dotnet test` を実行し、`LinePostbackActionServiceTests` を含む全テストが成功することを確認します（実チャネルがなくてもこのテストはモックで完結します）。
