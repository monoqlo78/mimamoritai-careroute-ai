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

## 未決事項

Blazor 側から本アプリの SQL へスナップショットを投入する仕組みは**未実装**です。
候補は (a) `AdminConsoleService` の出力を定期的に GraphQL API へ POST する HostedService、(b) Eventhouse 経由の疎結合。方式は未合意です。
