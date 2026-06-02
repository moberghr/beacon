import { Activity, AlertTriangle, Bell, TrendingUp } from 'lucide-react';
import {
  Banner,
  Card,
  CardBody,
  CardHead,
  CardSub,
  CardTitle,
  KPI,
  KPIGrid,
  Pill,
} from '@/components/beacon';
import { formatDateTime } from '@/lib/format';
import {
  ANOMALY_DETECTION_METHOD_LABEL,
  ANOMALY_SENSITIVITY_LABEL,
  useSubscriptionAnomalyChart,
  type SubscriptionDetail,
} from '../queries';

interface AnomalyTabProps {
  subscription: SubscriptionDetail;
}

export function AnomalyTab({ subscription }: AnomalyTabProps) {
  const cfg = subscription.anomalyConfig;
  const chart = useSubscriptionAnomalyChart(subscription.id, 30);

  if (!cfg || !cfg.enabled) {
    return (
      <div className="p-4">
        <div className="text-text-muted">
          Anomaly detection is not enabled for this subscription.
        </div>
      </div>
    );
  }

  const detectionLabel = ANOMALY_DETECTION_METHOD_LABEL[cfg.detectionMethod] ?? '—';
  const sensitivityLabel = ANOMALY_SENSITIVITY_LABEL[cfg.sensitivity] ?? '—';

  const data = chart.data;
  const recentEvents = (data?.points ?? [])
    .filter(p => p.isAnomaly || p.notificationSent)
    .slice()
    .sort((a, b) => +new Date(b.dateTime) - +new Date(a.dateTime))
    .slice(0, 10);

  return (
    <div className="p-4 flex flex-col gap-3">
      <Banner
        tone="info"
        icon={<Activity />}
        title="Anomaly detection active"
        sub={
          <>
            Monitors result counts using <strong>{detectionLabel}</strong> at{' '}
            <strong>{sensitivityLabel.toLowerCase()}</strong> sensitivity.
          </>
        }
      />

      <KPIGrid>
        <KPI dot="info" label="Detection method" value={detectionLabel} />
        <KPI dot="info" label="Sensitivity" value={sensitivityLabel} />
        <KPI dot="info" label="Lookback" value={`${cfg.lookbackDays} days`} />
        <KPI dot="info" label="Min data points" value={cfg.minimumDataPoints.toString()} />
      </KPIGrid>

      <div className="grid gap-3 grid-cols-1 md:grid-cols-2">
        <AlertCard
          tone={cfg.alertOnIncrease ? 'ok' : 'neutral'}
          icon={<TrendingUp className="size-3.5" />}
          title="Alert on increase"
          on={cfg.alertOnIncrease}
        />
        <AlertCard
          tone={cfg.alertOnDecrease ? 'warn' : 'neutral'}
          icon={<TrendingUp className="size-3.5" />}
          title="Alert on decrease"
          on={cfg.alertOnDecrease}
        />
      </div>

      {data?.hasAnomalyDetection && (
        <Card>
          <CardHead>
            <Activity className="size-3.5 text-text-muted" />
            <CardTitle>Result count trends &amp; thresholds</CardTitle>
            <CardSub>last 30 days</CardSub>
          </CardHead>
          <CardBody className="flex flex-col gap-2.5">
            <div className="flex gap-6 flex-wrap">
              <ThresholdStat label="Baseline" value={data.baselineMean} />
              <ThresholdStat label="Lower threshold" value={data.lowerThreshold} tone="warn" />
              <ThresholdStat label="Upper threshold" value={data.upperThreshold} tone="warn" />
            </div>

            {recentEvents.length > 0 && (
              <div>
                <div className="font-semibold mb-2">Recent detection events</div>
                <ul className="flex flex-col gap-1.5 p-0 m-0 list-none">
                  {recentEvents.map((p, i) => (
                    <li
                      key={`${p.dateTime}-${i}`}
                      className="flex items-center gap-2.5 px-2.5 py-1.5 border border-border rounded-sm"
                    >
                      {p.isAnomaly && <AlertTriangle className="size-3 text-warn" />}
                      {p.notificationSent && <Bell className="size-3 text-text-muted" />}
                      <span className="mono">{formatDateTime(p.dateTime)}</span>
                      <span className="text-text-muted">{p.resultCount.toLocaleString()} results</span>
                      {p.anomalySeverity && (
                        <Pill tone="warn" className="ml-auto">{p.anomalySeverity}</Pill>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </CardBody>
        </Card>
      )}
    </div>
  );
}

function AlertCard({
  tone, icon, title, on,
}: {
  tone: 'ok' | 'warn' | 'neutral';
  icon: React.ReactNode;
  title: string;
  on: boolean;
}) {
  return (
    <Card>
      <CardBody className="flex items-center gap-2.5">
        {icon}
        <div className="flex-1">
          <div className="font-semibold">{title}</div>
          <div className="text-text-muted text-xs">{on ? 'enabled' : 'disabled'}</div>
        </div>
        <Pill tone={tone}>{on ? 'ON' : 'OFF'}</Pill>
      </CardBody>
    </Card>
  );
}

function ThresholdStat({
  label, value, tone,
}: { label: string; value: number | null; tone?: 'warn' }) {
  return (
    <div>
      <div className="text-2xs font-semibold uppercase tracking-eyebrow text-text-muted">
        {label}
      </div>
      <div
        className={`text-xl font-semibold tracking-tighter ${tone === 'warn' ? 'text-warn' : ''}`}
      >
        {value == null ? '—' : Math.round(value).toLocaleString()}
      </div>
    </div>
  );
}
