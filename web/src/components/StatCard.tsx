import type { ReactNode } from 'react'

// A hairline figure card: Barlow label, IBM Plex Mono value, optional reason or context underneath.
export function StatCard({ label, value, hint }: { label: string; value: ReactNode; hint?: ReactNode }) {
  return (
    <div className="flex min-w-0 flex-col gap-1 border border-line bg-surface px-4 py-3">
      <span className="text-2xs font-semibold uppercase tracking-label text-text-muted">{label}</span>
      <span className="truncate font-mono text-lg text-text">{value}</span>
      {hint && <span className="text-xs text-text-muted">{hint}</span>}
    </div>
  )
}
