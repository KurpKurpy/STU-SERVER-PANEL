import { CaretLeftIcon, CaretRightIcon } from '@phosphor-icons/react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { OperationRow } from '../../lib/types'
import { OperationForm } from './OperationForm'
import { readinessText, statusTone, when } from './common'

type View = 'upcoming' | 'past' | 'calendar'

// Phase 6a: upcoming and past operations, and a month calendar asked for by the UTC range of the viewer's month (Ruling 13).
export function OperationsPage() {
  const can = useCan()
  const navigate = useNavigate()
  const [view, setView] = useState<View>('upcoming')
  const [month, setMonth] = useState(() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1) })
  const [creating, setCreating] = useState(false)

  // The calendar shows whole weeks, Monday first, so its range starts on the Monday on or before the 1st.
  const gridStart = new Date(month.getFullYear(), month.getMonth(), 1 - ((month.getDay() + 6) % 7))
  const gridEnd = new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate() + 42)
  const query = view === 'calendar' ? `?from=${gridStart.toISOString()}&to=${gridEnd.toISOString()}` : view === 'past' ? '?past=true' : ''
  const ops = useQuery({ queryKey: ['operations', view, view === 'calendar' ? month.toISOString() : ''], queryFn: () => api<OperationRow[]>(`/api/operations${query}`) })

  const tab = (v: View, label: string) => (
    <button type="button" aria-pressed={view === v} onClick={() => setView(v)}
      className={`min-h-11 border-b-2 px-3 text-sm font-semibold uppercase tracking-label ${view === v ? 'border-accent text-text' : 'border-transparent text-text-muted hover:text-text'}`}>
      {label}
    </button>
  )

  return (
    <>
      <Panel title="Operations" meta={can('operations.manage') && <Button variant="primary" size="sm" onClick={() => setCreating(true)}>New operation</Button>}>
        <div className="flex flex-wrap items-center gap-2 border-b border-line px-4">
          {tab('upcoming', 'Upcoming')}{tab('past', 'Past')}{tab('calendar', 'Calendar')}
        </div>
        {view === 'calendar' && (
          <div className="flex items-center justify-between gap-3 px-6 py-3">
            <Button size="sm" aria-label="Previous month" onClick={() => setMonth(m => new Date(m.getFullYear(), m.getMonth() - 1, 1))}><CaretLeftIcon size={16} aria-hidden /></Button>
            <span className="font-semibold">{month.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })}</span>
            <Button size="sm" aria-label="Next month" onClick={() => setMonth(m => new Date(m.getFullYear(), m.getMonth() + 1, 1))}><CaretRightIcon size={16} aria-hidden /></Button>
          </div>
        )}
        {ops.isPending && <div className="px-6 py-5"><Loading /></div>}
        {ops.error && <div className="px-6 py-5"><ErrorNote error={ops.error} onRetry={() => ops.refetch()} /></div>}
        {ops.data && view !== 'calendar' && <OperationList rows={ops.data} empty={view === 'past' ? 'No past operations yet.' : 'Nothing planned yet.'} />}
        {ops.data && view === 'calendar' && <MonthCalendar rows={ops.data} month={month} start={gridStart} />}
      </Panel>
      <Dialog open={creating} onClose={() => setCreating(false)} title="New operation">
        {creating && <OperationForm onDone={id => { setCreating(false); navigate(`/operations/${id}`) }} />}
      </Dialog>
    </>
  )
}

export function OperationList({ rows, empty }: { rows: OperationRow[]; empty: string }) {
  if (rows.length === 0) return <p className="m-0 px-6 py-5 text-text-muted">{empty}</p>
  return (
    <ul className="m-0 list-none p-0">
      {rows.map(o => (
        <li key={o.id} className="flex flex-col gap-1 border-t border-line px-6 py-3 sm:flex-row sm:items-center sm:gap-6">
          <span className="w-44 shrink-0 font-mono text-sm tabular-nums text-text-secondary">{when(o.startsAt)}</span>
          <span className="flex min-w-0 flex-1 flex-col gap-0.5">
            <Link to={`/operations/${o.id}`} className="font-semibold text-text hover:text-accent-text">{o.name}</Link>
            <span className="text-sm text-text-secondary">
              {o.mission ? `${o.mission.name} · ${o.mission.map}` : 'No mission yet'}
              {o.zeus.length > 0 && ` · Zeus ${o.zeus.map(z => z.name).join(', ')}`}
            </span>
          </span>
          <span className="text-sm text-text-secondary">{readinessText(o.readiness)}</span>
          {o.status !== 'Planned' && <StatusBlock tone={statusTone[o.status]}>{o.status}</StatusBlock>}
        </li>
      ))}
    </ul>
  )
}

// Six weeks from the Monday on or before the 1st. On phones it lists only the days that have operations.
function MonthCalendar({ rows, month, start }: { rows: OperationRow[]; month: Date; start: Date }) {
  const days = Array.from({ length: 42 }, (_, i) => new Date(start.getFullYear(), start.getMonth(), start.getDate() + i))
  const key = (d: Date) => `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`
  const byDay = new Map<string, OperationRow[]>()
  for (const o of rows) { const k = key(new Date(o.startsAt)); byDay.set(k, [...(byDay.get(k) ?? []), o]) }
  const today = key(new Date())
  const time = (iso: string) => new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
  const dayLabel = (d: Date) => d.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' })

  return (
    <>
      <div className="hidden border-t border-line md:grid md:grid-cols-7">
        {['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].map(d => (
          <span key={d} className="border-b border-line px-2 py-2 text-2xs font-semibold uppercase tracking-label text-text-muted">{d}</span>
        ))}
        {days.map(d => (
          <div key={key(d)} className={`min-h-24 border-b border-r border-line p-1.5 ${d.getMonth() === month.getMonth() ? '' : 'opacity-50'}`}>
            <span className={`text-xs tabular-nums ${key(d) === today ? 'font-bold text-accent-text' : 'text-text-muted'}`}>{d.getDate()}</span>
            {(byDay.get(key(d)) ?? []).map(o => (
              <Link key={o.id} to={`/operations/${o.id}`} className="mt-1 block truncate text-xs text-text hover:text-accent-text" title={o.name}>
                <span className="font-mono tabular-nums text-text-secondary">{time(o.startsAt)}</span> {o.name}
              </Link>
            ))}
          </div>
        ))}
      </div>
      <ul className="m-0 list-none border-t border-line p-0 md:hidden">
        {days.filter(d => d.getMonth() === month.getMonth() && byDay.has(key(d))).map(d => (
          <li key={key(d)} className="border-b border-line px-6 py-3">
            <span className="text-sm font-semibold">{dayLabel(d)}</span>
            {byDay.get(key(d))!.map(o => (
              <Link key={o.id} to={`/operations/${o.id}`} className="mt-1 block text-text hover:text-accent-text">
                <span className="font-mono tabular-nums text-text-secondary">{time(o.startsAt)}</span> {o.name}
              </Link>
            ))}
          </li>
        ))}
        {!days.some(d => d.getMonth() === month.getMonth() && byDay.has(key(d))) && <li className="px-6 py-5 text-text-muted">Nothing this month.</li>}
      </ul>
    </>
  )
}
