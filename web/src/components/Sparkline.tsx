interface Series { name: string; values: number[]; dashed?: boolean }

const W = 300
const H = 64

// A small history chart: the first series solid in the accent, a second series dashed and muted, so the two
// differ by line style as well as colour. Screen readers get the label, the latest value and the peak.
export function Sparkline({ label, series, format, max }: { label: string; series: Series[]; format: (n: number) => string; max?: number }) {
  const main = series[0]?.values ?? []
  const top = max ?? Math.max(1, ...series.flatMap(s => s.values))
  const now = main.length ? format(main[main.length - 1]) : '—'
  const summary = main.length ? `${label}, last 15 minutes: now ${now}, peak ${format(Math.max(...main))}` : `${label}: no data yet`
  const points = (values: number[]) =>
    values.map((v, i) => `${values.length === 1 ? W : (i / (values.length - 1)) * W},${H - 2 - (Math.min(v, top) / top) * (H - 4)}`).join(' ')

  return (
    <div className="flex min-w-0 flex-col gap-2 border border-line bg-surface px-4 py-3">
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-2xs font-semibold uppercase tracking-label text-text-muted">{label}</span>
        <span className="font-mono text-sm text-text">{now}</span>
      </div>
      <svg role="img" aria-label={summary} viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" className="h-16 w-full">
        <line x1="0" y1={H - 1} x2={W} y2={H - 1} className="stroke-line" vectorEffect="non-scaling-stroke" />
        {series.map((s, i) => s.values.length > 0 && (
          <polyline key={s.name} points={points(s.values)} fill="none" vectorEffect="non-scaling-stroke"
            strokeWidth={i === 0 ? 2 : 1.5} strokeDasharray={s.dashed ? '5 4' : undefined}
            className={i === 0 ? 'stroke-accent' : 'stroke-text-muted'} />
        ))}
      </svg>
      {series.length > 1 && (
        <span className="text-xs text-text-muted">{series.map((s, i) => `${s.name} ${i === 0 ? '(solid)' : '(dashed)'}`).join(' · ')}</span>
      )}
    </div>
  )
}
