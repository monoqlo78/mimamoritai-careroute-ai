# 見守り隊 運用コンソール (Fabric App / Rayfin)

Microsoft Fabric Apps (Rayfin) 上で動く、見守り隊の**運用者向け**コンソールです。
既存の Blazor アプリ (`src/MimamoriTai.Web`) の `/admin` と同じ観点を、Fabric 側のデータ基盤に載せて提供します。

- **Blazor 側 `/admin`** … アプリ DB を直接読む、リアルタイムの運用画面
- **本アプリ（Fabric）** … 非個人情報のスナップショットを Fabric の SQL に置き、Fabric SSO で運用者に開放する

## スコープと個人情報の扱い

本アプリは**居住者の個人情報を持ちません**。

- `HouseholdSnapshot` … 世帯ごとの件数・状態のみ（人数、デバイス数、SwitchBot 状態、LINE 受信者数、直近リスクレベル）
- `AlertRecord` … 通知の発生記録のみ。`WatchAlert.Message`（家族向け本文＝居住者名や行動を含みうる）は**意図的に持ち込みません**。機械生成の `reason` のみ保持します。

静的コンテンツは公開 URL から配信されるため、フロントエンドにシークレットを埋め込まないでください。

## 開発

```bash
# Fabric にデプロイしつつローカル開発サーバーを起動
npm run dev

# 初回のみ DB マイグレーションを適用
npm run rayfin:db
```

[http://localhost:5173](http://localhost:5173) を開きます。

Fabric に接続せず型と単体テストだけ確認したい場合:

```bash
npx tsc -b      # 型チェック（rayfin env を経由しない）
npm test        # vitest
npm run lint
```

ローカル実行時 (`isLocalBackend()`) は Fabric を呼ばずサンプルデータを返すため、Fabric 容量が無い状態でも UI を確認できます。

## 前提条件

- Fabric 容量の割り当て
- テナント管理者による **Fabric Apps ワークロードの有効化**

これらが無い場合 `rayfin up` は実行できません。

## データ投入（本番 → Fabric）

`scripts/` に本番 Azure SQL から Fabric へスナップショットを流し込む同期スクリプトがあります。
`rayfin up` でテーブルが作られた後に実行してください。

```bash
# 本番DBを読み取り、Fabric の SQL に MERGE する
pwsh ./scripts/sync-to-fabric.ps1
```

- `scripts/extract-snapshot.sql` … `AdminConsoleService.LoadAsync` と同じ集計を行う **読み取り専用**クエリ。`mimamori` スキーマのみを参照します
- `scripts/extract-snapshot.ps1` … 上記を実行して `snapshot.json` を出力（`.gitignore` 済み）
- `scripts/sync-to-fabric.ps1` … 抽出から Fabric への MERGE までを一括実行

認証は呼び出し元の Entra トークン（`az account get-access-token`）を使うため、
接続シークレットはリポジトリに保存されません。本番DBへの書き込みは行いません。

行のキーは冪等です（世帯は `householdId` から導出した固定 GUID、通知は元の `WatchAlert.Id`）。
そのため再実行しても重複しません。

`SwitchBotConnection.Encrypted*` と `WatchAlert.Message` は抽出クエリで**選択していません**。

## 構成

```text
├── rayfin/
│   ├── rayfin.yml                  # Fabric サービス構成 (auth/data/staticHosting)
│   └── data/
│       ├── HouseholdSnapshot.ts    # 世帯スナップショット（非個人情報）
│       ├── AlertRecord.ts          # 通知履歴（本文は持たない）
│       └── schema.ts               # 型付きクライアントが参照するスキーマ
├── src/
│   ├── main.tsx                    # エントリポイント + Rayfin クライアント初期化
│   ├── App.tsx                     # ルーティングと認証ゲート
│   ├── hooks/AuthContext.tsx       # 認証ヘルパーの React コンテキスト
│   ├── components/AuthPage.tsx     # サインイン UI
│   ├── pages/HomePage.tsx          # 運用コンソール UI
│   └── services/
│       ├── monitoring.ts           # 世帯・通知の取得と集計（純関数 summarize / sortHouseholds）
│       ├── rayfinClient.ts         # 型付き Rayfin クライアント
│       ├── MockAuthService.ts      # ローカル開発用（email/password）
│       └── RayfinAuthService.ts    # 本番用（Fabric ブローカー認証）
```

**新しいエンティティは必ず `rayfin/data/schema.ts` に登録してください。** SQL スキーマと GraphQL API はここから生成されるため、未登録のエンティティは実行時に存在しません。

## デプロイ

```bash
npx rayfin login
npx rayfin up --workspace-id <Fabric ワークスペース GUID>
npx rayfin up status
```

現在のデプロイ先は Fabric ワークスペース `CareRoute-AI-Mimamori`
(`e2a48a60-0b5f-421f-91bb-51a33fe528bc`) で、.NET 本体の `Fabric__WorkspaceId` と同じです。

`rayfin up` は静的コンテンツのビルド・配信とスキーマ適用を一度に行い、
デプロイ情報を `rayfin/.deployments.json`（gitignore 済み）に記録します。

デプロイ後は `scripts/sync-to-fabric.ps1` でデータを投入してください。

## 未決事項

- Blazor 側からの**自動**同期は未実装です。現状 `scripts/sync-to-fabric.ps1` を手動実行する運用で、
  定期実行（HostedService または Fabric パイプライン）は未着手です。
- 本アプリの `HouseholdSnapshot` / `AlertRecord` は `@role('authenticated', 'read')` の読み取り専用です。
  書き込みは上記スクリプトが Fabric SQL へ直接 MERGE する経路のみです。

