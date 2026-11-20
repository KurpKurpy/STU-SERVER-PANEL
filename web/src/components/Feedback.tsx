import { errorMessage } from '../lib/api'
import { Button } from './Button'

export function Loading({ label = 'Loading…' }: { label?: string }) {
  return <p role="status" className="m-0 text-sm text-text-muted">{label}</p>
}

export function ErrorNote({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return (
    <div role="alert" className="flex flex-wrap items-center gap-3 border border-accent/45 bg-tint px-4 py-3">
      <span className="text-accent-text">{errorMessage(error)}</span>
      {onRetry && <Button size="sm" onClick={onRetry}>Try again</Button>}
    </div>
  )
}

export function NoAccess() {
  return (
    <div className="border border-line-strong px-5 py-4 text-text-muted">
      You don't have access to this. Ask an admin if you think you should.
    </div>
  )
}

export function formatWhen(iso: string | null | undefined): string {
  if (!iso) return 'never'
  return new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
}
