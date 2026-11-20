import { StatusBlock, type Tone } from '../../components/StatusBlock'
import type { CheckResult, CheckState, OperationStatus, ReadinessInfo } from '../../lib/types'

// Times are stored in UTC and shown in the viewer's own zone (plan Global Constraints).
export const when = (iso: string) =>
  new Date(iso).toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

export function ago(iso: string) {
  const minutes = Math.round((Date.now() - Date.parse(iso)) / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  return hours < 48 ? `${hours} h ago` : `${Math.round(hours / 24)} days ago`
}

// "9 of 11 ready · checked 2 h ago", never a count nobody checked (Ruling 9).
export const readinessText = (r: ReadinessInfo | null) => (r ? `${r.ready} of ${r.total} ready · checked ${ago(r.checkedAt)}` : 'Not checked yet')

export const statusTone: Record<OperationStatus, Tone> = { Planned: 'ok', Done: 'off', Cancelled: 'off' }

export const stateWord: Record<CheckState, string> = { pass: 'Ready', fail: 'Not ready', unknown: "Can't tell", apply: 'Apply does this' }
export const stateTone: Record<CheckState, Tone> = { pass: 'ok', fail: 'bad', unknown: 'off', apply: 'warn' }

export function CheckState({ state }: { state: CheckState }) {
  return <StatusBlock tone={stateTone[state]}>{stateWord[state]}</StatusBlock>
}

// The skewed readiness bar (design §9 Segments): one segment per item, each state with its own colour; the label carries the words.
export function Segments({ results, label }: { results: CheckResult[]; label: string }) {
  const fill: Record<CheckState, string> = { pass: 'bg-ok', apply: 'bg-warn', fail: 'bg-line-strong', unknown: 'bg-off' }
  return (
    <div className="flex flex-col gap-2">
      <div role="img" aria-label={label} className="flex gap-1">
        {results.map(r => <span key={r.itemId} className={`h-3 flex-1 -skew-x-12 ${fill[r.state]}`} />)}
      </div>
      <span className="text-sm text-text-secondary">{label}</span>
    </div>
  )
}

// What's in a datetime-local box (the viewer's zone) as UTC, and back.
export const toLocalInput = (iso: string) => {
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}
export const fromLocalInput = (value: string) => new Date(value).toISOString()
