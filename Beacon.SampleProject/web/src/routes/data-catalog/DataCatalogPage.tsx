import { useMemo, useState } from 'react';
import { PageHeader } from '@/components/layout/PageHeader';
import { EmptyState } from '@/components/data/EmptyState';
import { Icon } from '@/components/Icon';
import { useDataCatalogQuery, type DataCatalogEntry } from './queries';

type QualityFilter = '' | 'high' | 'medium' | 'low' | 'unrated';

export default function DataCatalogPage() {
  const { data, isLoading, isError, error } = useDataCatalogQuery();
  const [search, setSearch] = useState('');
  const [dataSource, setDataSource] = useState('');
  const [schema, setSchema] = useState('');
  const [quality, setQuality] = useState<QualityFilter>('');

  const entries = data?.entries ?? [];

  const dataSources = useMemo(
    () => Array.from(new Set(entries.map(e => e.dataSourceName))).sort(),
    [entries],
  );
  const schemas = useMemo(
    () => Array.from(new Set(entries.map(e => e.schemaName))).sort(),
    [entries],
  );

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    return entries.filter(e => {
      if (term) {
        const haystack =
          `${e.tableName} ${e.schemaName} ${e.dataSourceName} ${e.description ?? ''}`.toLowerCase();
        if (!haystack.includes(term)) return false;
      }
      if (dataSource && e.dataSourceName !== dataSource) return false;
      if (schema && e.schemaName !== schema) return false;
      if (quality) {
        const s = e.qualityScore;
        if (quality === 'high' && !(s !== null && s >= 90)) return false;
        if (quality === 'medium' && !(s !== null && s >= 70 && s < 90)) return false;
        if (quality === 'low' && !(s !== null && s < 70)) return false;
        if (quality === 'unrated' && s !== null) return false;
      }
      return true;
    });
  }, [entries, search, dataSource, schema, quality]);

  const visible = filtered.slice(0, 100);

  return (
    <div className="page">
      <PageHeader
        title="Data catalog"
        sub="Browse and search all tables across your connected data sources."
      />

      <div className="card" style={{ padding: 16, marginBottom: 16 }}>
        <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))' }}>
          <label className="field">
            <span className="field__label">Search</span>
            <input
              className="input"
              type="search"
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder="Table name, schema, or column…"
            />
          </label>
          <label className="field">
            <span className="field__label">Data source</span>
            <select className="select" value={dataSource} onChange={e => setDataSource(e.target.value)}>
              <option value="">All</option>
              {dataSources.map(d => (
                <option key={d} value={d}>{d}</option>
              ))}
            </select>
          </label>
          <label className="field">
            <span className="field__label">Schema</span>
            <select className="select" value={schema} onChange={e => setSchema(e.target.value)}>
              <option value="">All</option>
              {schemas.map(s => (
                <option key={s} value={s}>{s}</option>
              ))}
            </select>
          </label>
          <label className="field">
            <span className="field__label">Quality</span>
            <select
              className="select"
              value={quality}
              onChange={e => setQuality(e.target.value as QualityFilter)}
            >
              <option value="">Any score</option>
              <option value="high">High (90%+)</option>
              <option value="medium">Medium (70–89%)</option>
              <option value="low">Low (under 70%)</option>
              <option value="unrated">Not rated</option>
            </select>
          </label>
        </div>
      </div>

      {isLoading && <p className="muted">Loading catalog…</p>}
      {isError && (
        <EmptyState
          title="Failed to load catalog"
          description={error instanceof Error ? error.message : 'Unknown error.'}
        />
      )}
      {!isLoading && !isError && filtered.length === 0 && (
        <EmptyState title="No tables found" description="Try adjusting your search or filters." />
      )}

      {filtered.length > 0 && (
        <div
          style={{
            display: 'grid',
            gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))',
            gap: 12,
          }}
        >
          {visible.map(e => (
            <CatalogCard key={`${e.dataSourceName}.${e.schemaName}.${e.tableName}`} entry={e} />
          ))}
        </div>
      )}

      {filtered.length > 100 && (
        <p className="muted" style={{ marginTop: 12 }}>
          Showing first 100 of {filtered.length} results. Refine search to see more.
        </p>
      )}
    </div>
  );
}

export function scorePillClass(score: number | null | undefined): string {
  if (score === null || score === undefined) return 'pill pill--neutral';
  if (score >= 90) return 'pill pill--ok';
  if (score >= 70) return 'pill pill--warn';
  return 'pill pill--crit';
}

function CatalogCard({ entry }: { entry: DataCatalogEntry }) {
  const score = entry.qualityScore;
  const scoreClass = scorePillClass(score);

  return (
    <div className="card" style={{ padding: 14 }}>
      <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: 8 }}>
        <div style={{ minWidth: 0 }}>
          <div className="muted" style={{ fontSize: 12 }}>{entry.schemaName}</div>
          <div style={{ fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis' }}>
            {entry.tableName}
          </div>
        </div>
        <span className={scoreClass}>{score === null ? 'N/A' : `${score.toFixed(0)}%`}</span>
      </div>

      <div style={{ display: 'flex', gap: 6, alignItems: 'center', marginTop: 8 }}>
        <Icon.Database aria-hidden />
        <span style={{ fontSize: 13 }}>{entry.dataSourceName}</span>
      </div>

      <hr style={{ margin: '10px 0', border: 0, borderTop: '1px solid var(--border)' }} />

      <div style={{ display: 'flex', gap: 16 }}>
        <div>
          <div style={{ fontWeight: 700, fontSize: 18 }}>{entry.columnCount}</div>
          <div className="muted" style={{ fontSize: 11 }}>Columns</div>
        </div>
        <div>
          <div style={{ fontWeight: 700, fontSize: 18 }}>{entry.codeReferenceCount}</div>
          <div className="muted" style={{ fontSize: 11 }}>Code refs</div>
        </div>
      </div>

      {entry.description && (
        <p
          className="muted"
          style={{
            margin: '8px 0 0',
            fontSize: 12,
            display: '-webkit-box',
            WebkitLineClamp: 2,
            WebkitBoxOrient: 'vertical',
            overflow: 'hidden',
          }}
        >
          {entry.description}
        </p>
      )}
    </div>
  );
}
