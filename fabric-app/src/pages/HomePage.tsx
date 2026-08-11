import { useCallback, useEffect, useState } from 'react';

import {
  ActivityArea,
  AlertTimeline,
  DeliveryGauge,
  DeviceBreakdown,
  HouseholdBars,
  RhythmHeatmap,
  RiskDonut,
  RouterModels,
} from '@/components/charts';
import { DataFlowCanvas } from '@/components/DataFlowCanvas';
import { useAuth } from '@/hooks/AuthContext';
import {
  activitySummary,
  alertsByDay,
  dailyActivity,
  deliveryStats,
  deviceBreakdown,
  hourlyRhythm,
  householdBars,
  pipelineStats,
  riskDistribution,
  routerModels,
  routerSummary,
} from '@/services/analytics';
import {
  getActivity,
  getAiRouterCalls,
  getAlerts,
  getDataOrigin,
  getHouseholds,
  summarize,
  SNAPSHOT_TAKEN_AT,
  type ActivityRow,
  type AiRouterCallRow,
  type AlertRow,
  type DataOrigin,
  type HouseholdRow,
} from '@/services/monitoring';
import { isLocalBackend } from '@/services/rayfinClient';

export function HomePage() {
  const { signOut, user } = useAuth();
  const [households, setHouseholds] = useState<HouseholdRow[]>([]);
  const [alerts, setAlerts] = useState<AlertRow[]>([]);
  const [activity, setActivity] = useState<ActivityRow[]>([]);
  const [aiCalls, setAiCalls] = useState<AiRouterCallRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [origin, setOrigin] = useState<DataOrigin>('fabric');

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [householdRows, alertRows, activityRows, aiRows] = await Promise.all([
        getHouseholds(),
        getAlerts(),
        // Activity is the newest table; tolerate a backend that predates it so
        // the rest of the console still renders.
        getActivity().catch(() => [] as ActivityRow[]),
        getAiRouterCalls().catch(() => [] as AiRouterCallRow[]),
      ]);
      setHouseholds(householdRows);
      setAlerts(alertRows);
      setActivity(activityRows);
      setAiCalls(aiRows);
      setOrigin(getDataOrigin());
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
  const flow = pipelineStats(households, alerts, activity, origin, aiCalls);
  const timeline = alertsByDay(alerts);
  const risks = riskDistribution(alerts);
  const delivery = deliveryStats(alerts);
  const bars = householdBars(households);
  const activityDaily = dailyActivity(activity);
  const rhythm = hourlyRhythm(activity);
  const devices = deviceBreakdown(activity);
  const activityTotals = activitySummary(activity);
  const aiModels = routerModels(aiCalls);
  const aiSummary = routerSummary(aiCalls);

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

      <main className="mx-auto max-w-7xl px-4 py-8 space-y-6">
        {isLocalBackend() && (
          <div className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            ローカル開発モードです。Fabric のバックエンドが未接続のため、
            サンプルデータを表示しています。
          </div>
        )}

        {!isLocalBackend() && origin === 'snapshot' && (
          <div className="rounded-xl border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900">
            <span className="font-semibold">Fabric SQL に接続できていません。</span>{' '}
            {SNAPSHOT_TAKEN_AT.toLocaleString('ja-JP')} 時点で本番データベースから
            抽出したスナップショットを表示しています。これは現在の状況ではありません。
            <span className="block mt-1 text-xs text-amber-800">
              Fabric の容量が一時停止している可能性があります。復旧すると自動的に
              最新データの表示に戻ります。
            </span>
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
          <div className="mb-4 flex flex-wrap items-end justify-between gap-2">
            <div>
              <h2 className="text-base font-semibold text-gray-900">
                データはこう流れています
              </h2>
              <p className="text-xs text-gray-500">
                センサーから家族への通知まで、そして Fabric に取り込まれて
                この画面に届くまで。数字はすべて下の表と同じ実データです。
              </p>
            </div>
            <div className="text-right text-xs text-gray-500">
              <div>
                最終イベント <span className="font-medium text-gray-800">{formatTime2(flow.lastEvent)}</span>
              </div>
              <div>
                最終同期 <span className="font-medium text-gray-800">{formatTime2(flow.lastSync)}</span>
              </div>
            </div>
          </div>
          <DataFlowCanvas stats={flow} />
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <div className="mb-4">
            <h2 className="text-base font-semibold text-gray-900">
              OrcaRouter が使ったモデル
            </h2>
            <p className="mt-1 text-xs text-gray-500">
              見守り隊の AI 呼び出しは OpenAI 互換の OrcaRouter を通ります。
              記録した {aiSummary.calls.toLocaleString()} 回に対し、実際に{' '}
              <span className="font-medium text-rose-700">
                {aiSummary.models} 種類
              </span>{' '}
              のモデルが応答しました（成功 {aiSummary.success.toLocaleString()} 回）。
              用途ごとに別ベンダのモデルへ振り分けられています。
            </p>
            <p className="mt-1 text-xs text-gray-500">
              下段の細い棒は平均応答時間です。LINE の webhook は 8 秒でイベントを
              打ち切るため、締切のある経路には速いモデルを使っています。
            </p>
            {aiSummary.mockCalls > 0 && (
              <p className="mt-1 text-[11px] text-gray-400">
                API キー未設定時のローカルスタブ {aiSummary.mockCalls} 回は
                OrcaRouter を経由していないため、上の集計から除いています。
              </p>
            )}
          </div>
          <RouterModels models={aiModels} />
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <div className="mb-4 flex flex-wrap items-end justify-between gap-2">
            <div>
              <h2 className="text-base font-semibold text-gray-900">
                機器イベントの推移
              </h2>
              <p className="text-xs text-gray-500">
                実線=総イベント数／点線=ON 判定。1 時間単位で集計した
                DeviceEvents を日次にまとめています。
              </p>
            </div>
            <div className="text-right text-xs text-gray-500">
              <div>
                <span className="font-medium text-gray-800">
                  {activityTotals.events.toLocaleString()}
                </span>{' '}
                イベント / {activityTotals.days} 日 / {activityTotals.devices} 台
              </div>
              {activityTotals.sources.length > 0 && (
                <div>取込元: {activityTotals.sources.join('、')}</div>
              )}
            </div>
          </div>

          {activity.length === 0 ? (
            <p className="text-sm text-gray-400">
              機器イベントがまだ取り込まれていません。
            </p>
          ) : (
            <>
              <ActivityArea points={activityDaily} />
              <div className="mt-6 grid gap-6 lg:grid-cols-2">
                <div>
                  <h3 className="text-sm font-semibold text-gray-800">
                    生活リズム（時間帯別・JST）
                  </h3>
                  <p className="mb-3 text-xs text-gray-500">
                    全期間の合計。暖色ほど活動が多い時間帯です。
                  </p>
                  <RhythmHeatmap cells={rhythm} />
                </div>
                <div>
                  <h3 className="text-sm font-semibold text-gray-800">機器別の寄与</h3>
                  <p className="mb-3 text-xs text-gray-500">イベント数の多い順</p>
                  <DeviceBreakdown slices={devices} />
                </div>
              </div>
            </>
          )}
        </section>

        <section className="grid gap-4 lg:grid-cols-3">
          <Panel title="通知の推移" sub="直近7日／赤は配信失敗">
            <AlertTimeline buckets={timeline} />
          </Panel>
          <Panel title="リスク内訳" sub="通知時点の判定">
            <RiskDonut slices={risks} />
          </Panel>
          <Panel title="LINE 配信結果" sub="取得済みの通知全件">
            <DeliveryGauge
              successRate={delivery.successRate}
              success={delivery.success}
              failed={delivery.failed}
            />
          </Panel>
        </section>

        <section className="rounded-xl border border-gray-200 bg-white p-5">
          <h2 className="mb-1 text-base font-semibold text-gray-900">世帯別の規模と通知量</h2>
          <p className="mb-4 text-xs text-gray-500">機器数の多い順</p>
          <HouseholdBars bars={bars} />
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

function Panel({
  title,
  sub,
  children,
}: {
  title: string;
  sub: string;
  children: React.ReactNode;
}) {
  return (
    <div className="rounded-xl border border-gray-200 bg-white p-5">
      <h2 className="text-base font-semibold text-gray-900">{title}</h2>
      <p className="mb-4 text-xs text-gray-500">{sub}</p>
      {children}
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

function formatTime2(date: Date | null): string {
  return date ? formatDate(date) : '—';
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
