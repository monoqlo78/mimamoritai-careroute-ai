# LINE Login を Entra External ID (CIAM) に組み込む手順

このドキュメントでは、LINE Login を Entra External ID (CIAM) テナントの外部 ID プロバイダーとして
組み込むための手順を説明します。**LINE Developers Console でのチャネル作成だけは自動化できない**
ため手動で行い、それ以降の Graph API 設定はすべて `scripts/setup-line-entra-idp.ps1` が自動実行します。

## 前提条件

- Entra External ID (CIAM) テナントに対する管理者権限
- Azure CLI (`az`) がインストール済みで、対象テナントにログインできること
- PowerShell 7 以降 (`pwsh`)
- LINE Developers アカウント (https://developers.line.biz/console/)

既知の環境値（このリポジトリのデフォルト値としてスクリプトに設定済み）:

| 項目 | 値 |
| --- | --- |
| CIAM テナント ID | `5ff64b34-cc0e-4813-9911-92968b7ff975` |
| アプリ登録 (Web アプリ) の App ID | `dcc221af-ceb0-47fe-baac-837e8853423c` |
| 既存のユーザーフロー ID | `d06ea237-ed42-4f1c-8526-9d766b66d8f4` |
| アプリ URL | `https://app-mimamoritai-hack.azurewebsites.net` |
| LINE プロバイダー (Provider) ID | `1581660279` |
| LINE OIDC issuer | `https://access.line.me` |
| LINE well-known エンドポイント | `https://access.line.me/.well-known/openid-configuration` |

> **注意:** LINE の OIDC 実装は `offline_access` スコープをサポートしていません。
> そのため、要求するスコープは `openid profile email` に固定しています。

---

## 手順 1: LINE Developers Console で LINE Login チャネルを作成する（手動）

1. https://developers.line.biz/console/ にログインします。
2. 既存のプロバイダー（Provider ID: `1581660279`）を選択します。
   - 表示されていない場合は、正しい LINE アカウントでログインしているか確認してください。
3. 「新規チャネル作成」から **LINE Login** チャネルを作成します。
   - チャネルの種類 (Channel type): **LINE Login**
   - アプリタイプ (App types): **ウェブアプリ (Web app)** にチェックを入れる
   - チャネル名・チャネル概要・大業種/小業種などの必須項目を入力
   - メールアドレスなど必要事項を入力して作成を完了させます
4. 作成したチャネルの **「LINEログイン設定」** タブを開き、以下を設定します。
   - **コールバックURL (Callback URL)**: 手順3で `setup-line-entra-idp.ps1` を実行した後に
     Entra 管理センターに表示される値を登録します（詳細は下記「コールバック URL について」参照）。
     暫定的に以下のいずれかの形式を仮登録し、スクリプト実行後に実際の値へ更新してください。
     - `https://<tenant-subdomain>.ciamlogin.com/5ff64b34-cc0e-4813-9911-92968b7ff975/federation/oidc/access.line.me`
     - `https://contsoexternal.ciamlogin.com/5ff64b34-cc0e-4813-9911-92968b7ff975/federation/oauth2`
5. **「OpenID Connect」タブ** を開き、`email` パーミッションを申請します。
   - `openid` と `profile` は標準で利用可能ですが、**`email` スコープは申請して承認されるまで
     利用できません**。申請フォームに利用目的（ユーザー識別・本人確認等）を記入して送信します。
   - 承認されるまで数営業日かかる場合があります。承認前は `email` クレームが返らず、
     Entra 側でメールアドレスに基づくユーザー識別ができない点に注意してください。

### Channel ID と Channel secret の確認場所

作成したチャネルの **「チャネル基本設定」タブ** に以下が表示されます。

- **チャネルID (Channel ID)** → スクリプトの `-LineChannelId` パラメーターに渡します。
- **チャネルシークレット (Channel secret)** → 「発行」ボタンを押して発行し、
  スクリプトの `-LineChannelSecret` パラメーターに渡します。
  - **このシークレットはコミットしたり、ログ・チャットに貼り付けたりしないでください。**

---

## 手順 2: セットアップスクリプトを実行する（自動）

Channel ID と Channel secret を取得したら、リポジトリのルートで以下を実行します。

```powershell
az login --tenant 5ff64b34-cc0e-4813-9911-92968b7ff975 --allow-no-subscriptions

./scripts/setup-line-entra-idp.ps1 `
    -LineChannelId "1234567890" `
    -LineChannelSecret "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
```

テナント ID・アプリ ID・ユーザーフロー ID を明示的に指定したい場合は次のように渡せます
（省略時は上記の既知の値がデフォルトとして使われます）。

```powershell
./scripts/setup-line-entra-idp.ps1 `
    -LineChannelId "1234567890" `
    -LineChannelSecret "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" `
    -TenantId "5ff64b34-cc0e-4813-9911-92968b7ff975" `
    -AppId "dcc221af-ceb0-47fe-baac-837e8853423c" `
    -UserFlowId "d06ea237-ed42-4f1c-8526-9d766b66d8f4"
```

スクリプトが行うこと:

1. `az account get-access-token` で CIAM テナント向けの Graph トークンを取得します。
   トークンが取得できない場合は `az login --tenant <TenantId> --allow-no-subscriptions`
   の実行を促して終了します。
2. 既存の `LINE` という表示名の OIDC 識別プロバイダーがあるかを確認します。
   - あれば、新しい Channel ID / Channel secret で **更新 (PATCH)** します。
   - なければ、新規に **作成 (POST)** します（作成に失敗した場合は `issuer` プロパティを
     追加した形で自動的にリトライします）。
3. 作成/更新した識別プロバイダーを、指定したユーザーフローに **リンク** します。
   （既にリンク済みの場合はエラーにせず成功として扱います。）
4. ユーザーフローを再読込し、現在有効になっている識別プロバイダーの一覧を表示して検証します。
5. 最後に、LINE Login チャネル側に登録すべき **コールバック URL の候補** を表示します。

### コールバック URL について

Microsoft Entra 管理センターの表示は、テナントやプロバイダーの設定によって以下のいずれかの
形式になることがあります。**必ず Entra 管理センター（External Identities > すべての ID
プロバイダー > LINE）に実際に表示される値を確認し、それを LINE Console に登録してください。**

- テナントサブドメイン形式: `https://<tenant-subdomain>.ciamlogin.com/<TenantId>/federation/oidc/access.line.me`
- 汎用 External ID コールバック形式: `https://contsoexternal.ciamlogin.com/<TenantId>/federation/oauth2`

---

## トラブルシューティング

### `email` スコープ関連のエラー（scope not approved）

- LINE Developers Console の「OpenID Connect」タブで `email` パーミッションの申請が
  承認されているか確認してください。未承認の間は Graph 側の設定を正しくしても
  メールクレームが返らず、ユーザーフローでメールアドレスが取得できません。
- 承認待ちの間は動作確認のため、一時的に `email` を含まない `openid profile` のみで
  テストすることもできますが、本番運用では `email` の承認完了を待ってください。

### リダイレクト URI (コールバック URL) が一致しない

- LINE 側のエラー画面で `400 Bad Request` や `redirect_uri does not match` が出る場合、
  Entra 管理センターに表示されている実際のコールバック URL と LINE Console に登録した URL が
  完全に一致しているか（末尾のスラッシュや大文字小文字含む）を確認してください。
- Microsoft はテナントによって表示するコールバック URL の形式を変えることがあるため、
  本ドキュメント記載の2パターンのうち推測ではなく、必ず管理センターの表示値をコピーして
  使用してください。

### `offline_access` スコープが使えない

- LINE の OIDC 実装は `offline_access`（リフレッシュトークン取得用スコープ）を
  サポートしていません。Entra 側の識別プロバイダー設定でこのスコープを含めると
  認可リクエストが失敗するため、`scripts/setup-line-entra-idp.ps1` では
  `openid profile email` のみを要求するようになっています。もし手動で設定を変更した
  場合は、この点を変えないよう注意してください。

### Graph API 呼び出しが 401/403 で失敗する

- `az login --tenant <TenantId> --allow-no-subscriptions` を再実行し、CIAM テナントに
  対して十分な権限（Identity Provider 管理者などのロール）を持つアカウントでサインイン
  しているか確認してください。
