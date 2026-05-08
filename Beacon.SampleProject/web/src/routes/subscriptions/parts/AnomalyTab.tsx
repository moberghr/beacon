import { Icon } from '@/components/Icon';
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
      <div style={{ padding: 16 }}>
        <div className="muted">Anomaly detection is not enabled for this subscription.</div>
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
    <div style={{ padding: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div className="callout">
        <Icon.Activity size={16} className="callout__icon" />
        <div>
          <div className="callout__title">Anomaly detection active</div>
          <div className="callout__sub">
            Monitors result counts using <strong>{detectionLabel}</strong> at{' '}
            <strong>{sensitivityLabel.toLowerCase()}</strong> sensitivity.
          </div>
        </div>
      </div>

      <div className="kpi-grid">
        <Tile label="Detection method" value={detectionLabel} />
        <Tile label="Sensitivity" value={sensitivityLabel} />
        <Tile label="Lookback" value={`${cfg.lookbackDays} days`} />
        <Tile label="Min data points" value={cfg.minimumDataPoints.toString()} />
      </div>

      <div className="row row--2-up">
        <AlertCard
          tone={cfg.alertOnIncrease ? 'ok' : 'neutral'}
          icon={<Icon.Trend size={14} />}
          title="Alert on increase"
          on={cfg.alertOnIncrease}
        />
        <AlertCard
          tone={cfg.alertOnDecrease ? 'warn' : 'neutral'}
          icon={<Icon.Trend size={14} />}
          title="Alert on decrease"
          on={cfg.alertOnDecrease}
        />
      </div>

      {data?.hasAnomalyDetection && (
        <div className="card">
          <div className="card__head">
            <Icon.Activity size={15} className="muted" />
            <h3 className="card__title">Result count trends &amp; thresholds</h3>
            <span className="card__sub">last 30 days</span>
          </div>
          <div className="card__body" style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div style={{ display: 'flex', gap: 24, flexWrap: 'wrap' }}>
              <ThresholdStat label="Baseline" value={data.baselineMean} />
              <ThresholdStat label="Lower threshold" value={data.lowerThreshold} tone="warn" />
              <ThresholdStat label="Upper threshold" value={data.upperThreshold} tone="warn" />
            </div>

            {recentEvents.length > 0 && (
              <div>
                <div style={{ fontWeight: 600, marginBottom: 8 }}>Recent detection events</div>
                <ul style={{ display: 'flex', flexDirection: 'column', gap: 6, padding: 0, margin: 0, listStyle: 'none' }}>
                  {recentEvents.map((p, i) => (
                    <li
                      key={`${p.dateTime}-${i}`}
                      style={{
                        display: 'flex',
                        alignItems: 'center',
                        gap: 10,
                        padding: '6px 10px',
                        border: '1px solid var(--border)',
                        borderRadius: 8,
                      }}
                    >
                      {p.isAnomaly && <Icon.Alert size={12} />}
                      {p.notificationSent && <Icon.Bell size={12} />}
                      <span className="mono">{formatDateTime(p.dateTime)}</span>
                      <span className="muted">{p.resultCount.toLocaleString()} results</span>
                      {p.anomalySeverity && (
                        <span
                          className="pill pill--warn mono"
                          style={{ fontSize: 10, marginLeft: 'auto' }}
                        >
                          {p.anomalySeverity}
                        </span>
                      )}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function Tile({ label, value }: { label: string; value: string }) {
  return (
    <div className="kpi">
      <div className="kpi__head">
        <span className="kpi__dot kpi__dot--info" />
        <span className="kpi__label">{label}</span>
      </div>
      <div className="kpi__value" style={{ fontSize: 18 }}>{value}</div>
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
    <div className="card">
      <div className="card__body" style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
        {icon}
        <div style={{ flex: 1 }}>
          <div style={{ fontWeight: 600 }}>{title}</div>
          <div className="muted">{on ? 'enabled' : 'disabled'}</div>
        </div>
        <span className={`pill pill--${tone === 'neutral' ? 'neutral' : tone}`}>
          {on ? 'ON' : 'OFF'}
        </span>
      </div>
    </div>
  );
}

function ThresholdStat({
  label, value, tone,
}: { label: string; value: number | null; tone?: 'warn' }) {
  return (
    <div>
      <div className="kpi__label">{label.toUpperCase()}</div>
      <div
        className="kpi__value"
        style={{ fontSize: 20, color: tone === 'warn' ? 'var(--warn)' : undefined }}
      >
        {value == null ? '—' : Math.round(value).toLocaleString()}
      </div>
    </div>
  );
}
