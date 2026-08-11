import { useCallback, useEffect, useState } from 'react';

import { useAuth } from '@/hooks/AuthContext';
import {
  getAlerts,
  getHouseholds,
  summarize,
  type AlertRow,
  type HouseholdRow,
} from '@/services/monitoring';
import { isLocalBackend } from '@/services/rayfinClient';

export function HomePage() {
  const { signOut, user } = useAuth();
  const [households, setHouseholds] = useState<HouseholdRow[]>([]);
  const [alerts, setAlerts] = useState<AlertRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [householdRows, alertRows] = await Promise.all([
        getHouseholds(),
        getAlerts(),
      ]);
      setHouseholds(householdRows);
      setAlerts(alertRows);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const totals = summarize(households);

  return (
    <div className="bg-gray-50 min-h-screen">
      <header className="flex items-center justify-between px-8 py-5 bg-white border-b border-gray-200">
        <div>
          <h1 className="text-xl font-bold text-gray-900">
            見守り隊 運用コンソール
          </h1>
          <p className="text-xs text-gray-500">
            Microsoft Fabric 上で全世帯の稼働状況を確認します
          </p>
        </div>
        <div className="flex items-center gap-4">
          <button
            onClick={() => void refresh()}
            className="rounded-lg border border-gray-300 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50"
          >
            更新
          </button>
          {user?.email && (
            <span className="text-sm text-gray-600" title={user.email}>
              {user.email}
            </span>
          )}
          <button
            onClick={() => void signOut()}
            className="text-gray-400 hover:text-gray-600 transition-colors text-sm"
          >
            サインアウト
          </button>
        </div>
      </header>

      <main className="max-w-6xl mx-auto px-4 py-8 space-y-6">
        {isLocalBackend() && (
          <div className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            ローカル開発モードです。Fabric のバックエンドが未接続のため、
            サンプルデータを表示しています。
          </div>
        )}

        {error && (
          <div className="rounded-xl border border-red-300 bg-red-50 px-4 py-3 text-sm text-red-800">
            読み込みに失敗しました: {error}
          </div>
        )}

        <section className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          <Kpi label="世帯" value={totals.households} sub={`本番 ${totals.production}`} />
          <Kpi label="デバイス" value={totals.devices} sub="全世帯合計" />
          <Kpi label="通知" value={totals.alerts} sub={`失敗 ${totals.failedAlerts}`} />
          <Kpi
            label="要対応"
            value={totals.needingAttention}
            sub="世帯"
            alert={totals.needingAttention > 0}
          />
          <Kpi label="通知失敗" value={totals.failedAlerts} sub="直近期間" alert={totals.failedAlerts > 0} />
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <h2 className="mb-4 text-base font-semibold text-gray-900">世帯一覧</h2>
          {loading ? (
            <p className="text-sm text-gray-400">読み込み中…</p>
          ) : households.length === 0 ? (
            <p className="text-sm text-gray-400">
              スナップショットがまだ届いていません。
            </p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs uppercase tracking-wide text-gray-400">
                    <Th>世帯</Th>
                    <Th>データ</Th>
                    <Th>家族</Th>
                    <Th>機器</Th>
                    <Th>最終イベント</Th>
                    <Th>SwitchBot</Th>
                    <Th>LINE</Th>
                    <Th>通知/失敗</Th>
                    <Th>リスク</Th>
                  </tr>
                </thead>
                <tbody>
                  {households.map((row) => (
                    <tr
                      key={row.id}
                      className={`border-t border-gray-100 ${
                        row.needsAttention ? 'bg-red-50/60' : ''
                      }`}
                    >
                      <Td>
                        <span className="font-medium text-gray-900">{row.name}</span>
                        {row.needsAttention && (
                          <span className="ml-2 rounded-full bg-red-100 px-2 py-0.5 text-[11px] text-red-700">
                            要対応
                          </span>
                        )}
                      </Td>
                      <Td>{row.dataSourceMode === 'Sample' ? 'デモ' : '本番'}</Td>
                      <Td>{row.memberCount}</Td>
                      <Td>{row.deviceCount}</Td>
                      <Td>{formatTime(row.lastEventUtc)}</Td>
                      <Td>
                        {switchBotLabel(row.switchBotStatus)}
                        {row.switchBotError && (
                          <div className="text-[11px] text-gray-500">
                            {row.switchBotError}
                          </div>
                        )}
                      </Td>
                      <Td>{row.activeLineRecipients}</Td>
                      <Td>
                        {row.alertsInWindow} /{' '}
                        <span
                          className={
                            row.failedAlertsInWindow !== '0'
                              ? 'text-red-600'
                              : undefined
                          }
                        >
                          {row.failedAlertsInWindow}
                        </span>
                      </Td>
                      <Td>{riskLabel(row.latestRiskLevel)}</Td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <h2 className="mb-4 text-base font-semibold text-gray-900">直近の通知</h2>
          {loading ? (
            <p className="text-sm text-gray-400">読み込み中…</p>
          ) : alerts.length === 0 ? (
            <p className="text-sm text-gray-400">通知はありません。</p>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="text-left text-xs uppercase tracking-wide text-gray-400">
                    <Th>日時</Th>
                    <Th>世帯</Th>
                    <Th>リスク</Th>
                    <Th>スコア</Th>
                    <Th>理由</Th>
                    <Th>結果</Th>
                  </tr>
                </thead>
                <tbody>
                  {alerts.map((alert) => (
                    <tr key={alert.id} className="border-t border-gray-100">
                      <Td>{formatDate(alert.sentAt)}</Td>
                      <Td>{alert.householdName}</Td>
                      <Td>{riskLabel(alert.riskLevel)}</Td>
                      <Td>{alert.score}</Td>
                      <Td>{alert.reason}</Td>
                      <Td>
                        {alert.success ? (
                          <span className="rounded-full bg-gray-100 px-2 py-0.5 text-[11px] text-gray-700">
                            成功
                          </span>
                        ) : (
                          <>
                            <span className="rounded-full bg-red-100 px-2 py-0.5 text-[11px] text-red-700">
                              失敗
                            </span>
                            {alert.error && (
                              <div className="text-[11px] text-gray-500">
                                {alert.error}
                              </div>
                            )}
                          </>
                        )}
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </main>
    </div>
  );
}

function Kpi({
  label,
  value,
  sub,
  alert,
}: {
  label: string;
  value: number;
  sub: string;
  alert?: boolean;
}) {
  return (
    <div
      className={`rounded-xl border bg-white p-4 ${
        alert ? 'border-red-300' : 'border-gray-200'
      }`}
    >
      <div className="text-xs text-gray-500">{label}</div>
      <div className="mt-1 text-2xl font-semibold text-gray-900">{value}</div>
      <div className="text-xs text-gray-400">{sub}</div>
    </div>
  );
}

function Th({ children }: { children: React.ReactNode }) {
  return <th className="whitespace-nowrap px-3 py-2 font-semibold">{children}</th>;
}

function Td({ children }: { children: React.ReactNode }) {
  return <td className="whitespace-nowrap px-3 py-2 align-top">{children}</td>;
}

function formatTime(iso: string): string {
  if (!iso) return '—';
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? '—' : formatDate(date);
}

function formatDate(date: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(date.getMonth() + 1)}/${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function switchBotLabel(status: string): string {
  switch (status) {
    case 'Connected':
      return '接続済み';
    case 'Error':
      return 'エラー';
    case 'NotConfigured':
    case '':
      return '未設定';
    default:
      return status;
  }
}

function riskLabel(level: string): string {
  switch (level) {
    case 'Low':
      return '低';
    case 'Medium':
      return '中';
    case 'High':
      return '高';
    default:
      return '—';
  }
}
