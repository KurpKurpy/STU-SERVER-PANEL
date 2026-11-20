import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type ReactNode } from 'react'
import { Link, useParams } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { ApiError, api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { CheckResult, ChecklistItemDto, OperationApplyPreview, OperationDto } from '../../lib/types'
import { OperationForm } from './OperationForm'
import { CheckState, Segments, readinessText, statusTone, when } from './common'

export function OperationPage() {
  const { id } = useParams()
  const can = useCan()
  const queryClient = useQueryClient()
  const op = useQuery({ queryKey: ['operation', id], queryFn: () => api<OperationDto>(`/api/operations/${id}`) })
  const [editing, setEditing] = useState(false)
  const [formKey, setFormKey] = useState(0)
  // The form opens on, and reloads to, the latest version; while open it keeps what it was opened on.
  const openEdit = async () => { await op.refetch(); setFormKey(k => k + 1); setEditing(true) }
  const reloadEdit = async () => { await op.refetch(); setFormKey(k => k + 1) }
  const [applying, setApplying] = useState(false)
  const refresh = () => Promise.all([queryClient.invalidateQueries({ queryKey: ['operation', id] }), queryClient.invalidateQueries({ queryKey: ['operations'] })])
  const status = useMutation({
    mutationFn: (s: string) => api(`/api/operations/${id}/status`, { json: { status: s } }),
    onSuccess: refresh,
  })

  if (op.isPending) return <Loading />
  if (op.error) return <ErrorNote error={op.error} onRetry={() => op.refetch()} />
  const o = op.data
  const facts: [string, ReactNode][] = [
    ['When', when(o.startsAt)],
    ['Mission', o.mission ? <Link to={`/missions/${o.mission.id}`}>{o.mission.name}{o.versionNumber ? ` v${o.versionNumber}` : ''}</Link> : '—'],
    ['Map', o.mission?.map ?? '—'],
    ['Server profile', o.profile?.name ?? '—'],
    ['Mod list', o.modList?.name ?? '—'],
    ['Mission maker', o.missionMaker?.name ?? '—'],
    ['Zeus', o.zeus.length ? o.zeus.map(z => z.name).join(', ') : '—'],
    ['Applied', o.appliedAt ? `${formatWhen(o.appliedAt)}${o.appliedBy ? ` by ${o.appliedBy.name}` : ''}` : 'Not yet'],
  ]

  return (
    <div className="stagger grid grid-cols-1 items-start gap-6 md:grid-cols-12">
      <Panel title={o.name} className="md:col-span-7" meta={
        <div className="flex flex-wrap items-center gap-2">
          <StatusBlock tone={statusTone[o.status]}>{o.status}</StatusBlock>
          {can('operations.manage') && <Button size="sm" onClick={openEdit}>Edit</Button>}
        </div>
      }>
        {o.description && <p className="m-0 whitespace-pre-wrap px-6 pt-4 text-text-secondary">{o.description}</p>}
        <dl className="m-0 grid grid-cols-1 gap-x-6 gap-y-3 px-6 py-5 sm:grid-cols-2">
          {facts.map(([k, v]) => (
            <div key={k} className="flex flex-col gap-0.5">
              <dt className="text-2xs font-semibold uppercase tracking-label text-text-muted">{k}</dt>
              <dd className="m-0 break-words">{v}</dd>
            </div>
          ))}
        </dl>
        {can('operations.manage') && (
          <div className="flex flex-wrap gap-2 border-t border-line px-6 py-3">
            {o.status !== 'Done' && <Button size="sm" busy={status.isPending} onClick={() => status.mutate('Done')}>Mark done</Button>}
            {o.status !== 'Cancelled' && <Button size="sm" busy={status.isPending} onClick={() => status.mutate('Cancelled')}>Cancel operation</Button>}
            {o.status !== 'Planned' && <Button size="sm" busy={status.isPending} onClick={() => status.mutate('Planned')}>Back to planned</Button>}
          </div>
        )}
      </Panel>

      <ReadinessPanel op={o} onChanged={refresh} onApply={() => setApplying(true)} />

      <Panel title="Briefing" className="md:col-span-7">
        <p className="m-0 whitespace-pre-wrap px-6 py-5">{o.briefing || <span className="text-text-muted">No briefing yet.</span>}</p>
      </Panel>
      {o.notes !== null && (
        <Panel title="Mission notes" className="md:col-span-5" meta="Planners and Zeus">
          <p className="m-0 whitespace-pre-wrap px-6 py-5">{o.notes || <span className="text-text-muted">No notes.</span>}</p>
        </Panel>
      )}

      <Dialog open={editing} onClose={() => setEditing(false)} title={`Edit ${o.name}`}>
        {editing && <OperationForm key={formKey} op={o} onReload={reloadEdit} onDone={async () => { setEditing(false); await refresh() }} />}
      </Dialog>
      <Dialog open={applying} onClose={() => setApplying(false)} title="Apply to server">
        {applying && <ApplyConfirm op={o} onDone={async () => { setApplying(false); await refresh() }} />}
      </Dialog>
    </div>
  )
}

// The checklist: the last Prepare's results with when they were checked, or the items unchecked; manual items can be ticked.
function ReadinessPanel({ op, onChanged, onApply }: { op: OperationDto; onChanged: () => Promise<unknown>; onApply: () => void }) {
  const can = useCan()
  const items = useQuery({ queryKey: ['checklist'], queryFn: () => api<ChecklistItemDto[]>('/api/checklist') })
  const prepare = useMutation({ mutationFn: () => api(`/api/operations/${op.id}/prepare`, { method: 'POST' }), onSuccess: onChanged })
  const tick = useMutation({
    mutationFn: ({ itemId, done }: { itemId: string; done: boolean }) => api(`/api/operations/${op.id}/checks/${itemId}`, { method: 'PUT', json: { done } }),
    onSuccess: onChanged,
  })
  const enabled = (items.data ?? []).filter(i => i.enabled)
  const rows: (CheckResult | null)[] = op.lastCheck ?? enabled.map(() => null)
  const blocked = !op.lastCheck || op.lastCheck.some(r => r.state === 'fail' || r.state === 'unknown')
  const manual = new Set(enabled.filter(i => i.autoKey === null).map(i => i.id))
  const planned = op.status === 'Planned'

  return (
    <Panel title="Readiness" className="md:col-span-5" emphasis={planned && !!op.lastCheck && blocked} meta={
      <div className="flex flex-wrap gap-2">
        {can('operations.prepare') && planned && <Button size="sm" busy={prepare.isPending} onClick={() => prepare.mutate()}>Prepare</Button>}
        {can('operations.apply') && planned && <Button size="sm" variant="primary" disabled={blocked} onClick={onApply}>Apply to server</Button>}
      </div>
    }>
      <div className="px-6 pt-4">
        {op.lastCheck && op.readiness
          ? <Segments results={op.lastCheck} label={readinessText(op.readiness)} />
          : <p className="m-0 text-sm text-text-secondary">Not checked yet. Prepare checks every item now.</p>}
        {prepare.error && <p role="alert" className="m-0 mt-2 text-sm text-accent-text">{errorMessage(prepare.error)}</p>}
        {tick.error && <p role="alert" className="m-0 mt-2 text-sm text-accent-text">{errorMessage(tick.error)}</p>}
      </div>
      <ul className="m-0 mt-3 list-none p-0">
        {rows.map((r, i) => {
          const item = r ?? null
          const id = item?.itemId ?? enabled[i]?.id
          const label = item?.label ?? enabled[i]?.label
          const isManual = !!id && manual.has(id)
          return (
            <li key={id ?? i} className="flex flex-col gap-1 border-t border-line px-6 py-3">
              <div className="flex flex-wrap items-center gap-2">
                {item ? <CheckState state={item.state} /> : <StatusBlock tone="off">Not checked</StatusBlock>}
                {isManual && can('operations.prepare') && planned ? (
                  <label className="flex min-h-9 items-center gap-2 font-semibold">
                    <input type="checkbox" checked={op.ticked.includes(id!)} disabled={tick.isPending}
                      onChange={e => tick.mutate({ itemId: id!, done: e.target.checked })} />
                    {label}
                  </label>
                ) : <span className="font-semibold">{label}</span>}
              </div>
              {item?.detail && <span className="text-sm text-text-secondary">{item.detail}</span>}
              {item?.fix && item.state !== 'pass' && <Link to={item.fix} className="text-sm">Fix it there</Link>}
            </li>
          )
        })}
      </ul>
    </Panel>
  )
}

// Built only from the preview, fetched fresh on open: the server, operation and player count it names are the ones the click sends back
// (final review I-3, M-6). A changed operation or player count refetches it, so the next click confirms what's shown.
function ApplyConfirm({ op, onDone }: { op: OperationDto; onDone: () => void }) {
  const queryClient = useQueryClient()
  const preview = useQuery({ queryKey: ['operation-apply', op.id], queryFn: () => api<OperationApplyPreview>(`/api/operations/${op.id}/apply`), staleTime: 0, gcTime: 0 })
  const apply = useMutation({
    mutationFn: (p: OperationApplyPreview) => api(`/api/operations/${op.id}/apply`, { json: { players: p.players, version: p.version } }),
    onSuccess: onDone,
    onError: e => {
      if (!(e instanceof ApiError) || (e.code !== 'stale_players' && e.code !== 'stale')) return
      preview.refetch()
      if (e.code === 'stale') queryClient.invalidateQueries({ queryKey: ['operation', op.id] })
    },
  })
  if (preview.isPending) return <Loading />
  if (preview.error) return <ErrorNote error={preview.error} onRetry={() => preview.refetch()} />
  const p = preview.data
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-text-secondary"><strong className="text-text">{p.operationName}</strong> on <strong className="text-text">{p.profileName}</strong>:</p>
      <ul className="m-0 flex list-disc flex-col gap-1.5 pl-5 text-sm">
        {p.action === 'restart'
          ? <li><strong>Restarts the server</strong>{p.players ? `; ${p.players} player${p.players === 1 ? '' : 's'} will be disconnected` : ''}.</li>
          : <li>Starts the server.</li>}
        <li>Sets the mission rotation to {p.fileName.replace(/\.pbo$/i, '')} only.</li>
        {p.modListChanges && <li>Switches the mod list to <strong>{p.modListName}</strong>.</li>}
        {p.unappliedChanges && <li>Also applies changes saved on the profile since it was last applied.</li>}
      </ul>
      {apply.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(apply.error)}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="primary" busy={apply.isPending} onClick={() => apply.mutate(p)}>{p.action === 'restart' ? 'Restart with this operation' : 'Start with this operation'}</Button>
      </div>
    </div>
  )
}
