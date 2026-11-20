import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Field, SelectField, TextArea } from '../../components/Field'
import { ApiError, api, errorMessage } from '../../lib/api'
import type { OperationDto, OperationOptions } from '../../lib/types'
import { fromLocalInput, toLocalInput } from './common'

// Create or edit an operation. An edit carries the version it was opened on, pinned so a background refetch can't swap it under the
// user's typing; a stale one is refused (Ruling 6), and onReload remounts the form with the latest version (final review I-4, I-5).
export function OperationForm({ op, onDone, onReload }: { op?: OperationDto; onDone: (id: string) => void; onReload?: () => void }) {
  const options = useQuery({ queryKey: ['operation-options'], queryFn: () => api<OperationOptions>('/api/operations/options') })
  const [missionId, setMissionId] = useState(op?.mission?.id ?? '')
  const [versionId, setVersionId] = useState(op?.missionVersionId ?? '')
  const [zeus, setZeus] = useState<string[]>(op?.zeus.map(z => z.id) ?? [])
  const [version] = useState(op?.version)
  const [error, setError] = useState<{ text: string; field?: string; stale?: boolean } | null>(null)
  const [busy, setBusy] = useState(false)

  if (options.isPending) return <Loading />
  if (options.error) return <ErrorNote error={options.error} onRetry={() => options.refetch()} />
  const o = options.data
  const mission = o.missions.find(m => m.id === missionId)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const data = new FormData(e.currentTarget)
    const text = (k: string) => String(data.get(k) ?? '')
    const id = (k: string) => text(k) || null
    const start = text('startsAt')
    if (!start) { setError({ text: 'Pick a date and time.', field: 'startsAt' }); return }
    const body = {
      version, name: text('name'), description: text('description'), startsAt: fromLocalInput(start), missionId: missionId || null,
      missionVersionId: versionId || null, profileId: id('profileId'), modListId: id('modListId'), missionMakerId: id('missionMakerId'), zeusIds: zeus,
      notes: text('notes'), briefing: text('briefing'),
    }
    setBusy(true)
    setError(null)
    try {
      if (op) { await api(`/api/operations/${op.id}`, { method: 'PUT', json: body }); onDone(op.id) }
      else onDone((await api<{ id: string }>('/api/operations', { json: body })).id)
    } catch (x) {
      const stale = x instanceof ApiError && x.code === 'stale'
      setError({ text: errorMessage(x), field: x instanceof ApiError && !stale ? x.field : undefined, stale })
    } finally { setBusy(false) }
  }

  const fieldError = (name: string) => (error?.field === name ? error.text : undefined)
  return (
    <form className="flex flex-col gap-4" onSubmit={submit}>
      <Field label="Name" name="name" defaultValue={op?.name} required maxLength={120} error={fieldError('name')} />
      <Field label="Date and time (your time zone)" name="startsAt" type="datetime-local" defaultValue={op ? toLocalInput(op.startsAt) : ''} required error={fieldError('startsAt')} />
      <TextArea label="Description" name="description" defaultValue={op?.description} rows={2} maxLength={2000} />
      <div className="grid gap-4 sm:grid-cols-2">
        <SelectField label="Mission" value={missionId} onChange={e => { setMissionId(e.target.value); setVersionId('') }}>
          <option value="">None yet</option>
          {o.missions.map(m => <option key={m.id} value={m.id}>{m.name} ({m.map})</option>)}
        </SelectField>
        <SelectField label="Version" value={versionId} onChange={e => setVersionId(e.target.value)} disabled={!mission}
          hint={mission ? 'Empty means the current version.' : undefined}>
          <option value="">{mission ? 'Current version' : '—'}</option>
          {mission?.versions.map(v => <option key={v.id} value={v.id}>v{v.number}</option>)}
        </SelectField>
        <SelectField label="Server profile" name="profileId" defaultValue={op?.profile?.id ?? ''}>
          <option value="">None yet</option>
          {o.profiles.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </SelectField>
        <SelectField label="Mod list" name="modListId" defaultValue={op?.modList?.id ?? ''}>
          <option value="">None yet</option>
          {o.modLists.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}
        </SelectField>
        <SelectField label="Mission maker" name="missionMakerId" defaultValue={op?.missionMaker?.id ?? ''}>
          <option value="">Nobody</option>
          {o.people.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </SelectField>
      </div>
      <fieldset className="m-0 flex flex-col gap-2 border border-line p-3">
        <legend className="px-1 text-2xs font-semibold uppercase tracking-label text-text-muted">Zeus</legend>
        <div className="flex max-h-40 flex-col gap-1 overflow-y-auto">
          {o.people.map(p => (
            <label key={p.id} className="flex min-h-9 items-center gap-2 text-sm">
              <input type="checkbox" checked={zeus.includes(p.id)} onChange={e => setZeus(z => (e.target.checked ? [...z, p.id] : z.filter(x => x !== p.id)))} />
              {p.name}
            </label>
          ))}
        </div>
      </fieldset>
      <TextArea label="Player briefing (everyone who can see operations)" name="briefing" defaultValue={op?.briefing} rows={4} maxLength={20000} />
      <TextArea label="Mission notes (planners and Zeus only)" name="notes" defaultValue={op?.notes ?? ''} rows={3} maxLength={10000} />
      {error?.stale ? (
        <div role="alert" className="flex flex-col gap-2 border border-accent p-3 text-sm">
          <p className="m-0">Someone else changed this operation while you were editing, so this wasn't saved. Copy anything you want to keep, then load their version and make your changes again.</p>
          {onReload && <div><Button size="sm" onClick={onReload}>Load their version</Button></div>}
        </div>
      ) : error && !error.field && <p role="alert" className="m-0 text-sm text-accent-text">{error.text}</p>}
      <div className="flex flex-wrap justify-end gap-3"><Button type="submit" variant="primary" busy={busy}>{op ? 'Save' : 'Create operation'}</Button></div>
    </form>
  )
}
