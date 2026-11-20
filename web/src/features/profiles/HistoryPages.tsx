import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useSearchParams } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { DiffView } from '../../components/DiffView'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Field, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { FileDiff, JsonValue, PresetDto, ProfileSummary, RevisionDto } from '../../lib/types'
import { useProfile } from './ProfileLayout'
import { SettingsForm } from './SettingsForm'

export function RevisionsTab() {
  const { draft } = useProfile()
  const can = useCan()
  const queryClient = useQueryClient()
  const p = draft.detail.data!
  const revisions = useQuery({ queryKey: ['revisions', p.id], queryFn: () => api<RevisionDto[]>(`/api/profiles/${p.id}/revisions`) })
  const [from, setFrom] = useState(Math.max(1, (p.appliedRevision ?? p.revision - 1) || 1))
  const [to, setTo] = useState(p.revision)
  const diff = useQuery({
    queryKey: ['revisions', p.id, 'compare', from, to],
    queryFn: () => api<{ files: FileDiff[] }>(`/api/profiles/${p.id}/compare?from=${from}&to=${to}`),
    enabled: from !== to,
  })
  const [resetting, setResetting] = useState<number | null>(null)
  const reset = useMutation({
    mutationFn: (n: number) => api(`/api/profiles/${p.id}/revisions/${n}/reset`, { json: { baseRevision: p.revision } }),
    onSuccess: async () => {
      setResetting(null)
      await Promise.all(['profile', 'profiles', 'servers', 'revisions'].map(k => queryClient.invalidateQueries({ queryKey: [k] })))
    },
  })
  const numbers = revisions.data?.map(r => r.number) ?? []

  return (
    <div className="flex flex-col gap-4">
      <Panel title="Revisions">
        {revisions.isPending && <div className="px-6 py-5"><Loading /></div>}
        {revisions.error && <div className="px-6 py-5"><ErrorNote error={revisions.error} onRetry={() => revisions.refetch()} /></div>}
        {revisions.data && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-left">
              <thead><tr className="text-2xs font-semibold uppercase tracking-label text-text-muted">
                {['#', 'Note', 'By', 'When', '', ''].map((h, i) => <th key={i} scope="col" className="px-6 py-3 font-semibold">{h}</th>)}
              </tr></thead>
              <tbody>
                {revisions.data.map(r => (
                  <tr key={r.number} className="border-t border-line">
                    <td className="px-6 py-3 font-mono">r{r.number}</td>
                    <td className="px-6 py-3">{r.note ?? '—'}</td>
                    <td className="px-6 py-3 text-text-secondary">{r.createdBy ?? '—'}</td>
                    <td className="px-6 py-3 whitespace-nowrap text-text-secondary">{formatWhen(r.createdAt)}</td>
                    <td className="px-6 py-3">{r.applied && <StatusBlock tone="ok">Applied</StatusBlock>}</td>
                    <td className="px-6 py-3 text-right">
                      {can('profiles.manage') && r.number !== p.revision && <Button size="sm" onClick={() => { reset.reset(); setResetting(r.number) }}>Reset to this</Button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <Panel title="Compare revisions">
        <div className="flex flex-col gap-4 px-6 py-5">
          <div className="grid grid-cols-2 gap-4 sm:max-w-md">
            <SelectField label="From" value={from} onChange={e => setFrom(Number(e.target.value))}>{numbers.map(n => <option key={n} value={n}>r{n}</option>)}</SelectField>
            <SelectField label="To" value={to} onChange={e => setTo(Number(e.target.value))}>{numbers.map(n => <option key={n} value={n}>r{n}</option>)}</SelectField>
          </div>
          {from === to ? <p className="m-0 text-text-muted">Pick two different revisions.</p>
            : diff.isPending ? <Loading /> : diff.error ? <ErrorNote error={diff.error} /> : <DiffView files={diff.data!.files} />}
        </div>
      </Panel>
      <Dialog open={resetting !== null} onClose={() => setResetting(null)} title={`Reset to revision ${resetting ?? ''}?`}>
        <p className="m-0 max-w-prose text-text-secondary">
          Creates revision {p.revision + 1} with revision {resetting}'s settings and passwords. The server keeps running what was applied until you apply again.
        </p>
        {reset.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(reset.error)}</p>}
        <div className="flex justify-end gap-2.5">
          <Button onClick={() => setResetting(null)}>Cancel</Button>
          <Button variant="primary" busy={reset.isPending} onClick={() => resetting && reset.mutate(resetting)}>Reset</Button>
        </div>
      </Dialog>
    </div>
  )
}

export function ComparePage() {
  const can = useCan()
  const [params, setParams] = useSearchParams()
  const a = params.get('a') ?? ''
  const b = params.get('b') ?? ''
  const profiles = useQuery({ queryKey: ['profiles'], queryFn: () => api<ProfileSummary[]>('/api/profiles'), enabled: can('profiles.view') })
  const result = useQuery({
    queryKey: ['profiles', 'compare', a, b],
    queryFn: () => api<{ identity: { field: string; a: string | null; b: string | null }[]; files: FileDiff[] }>(`/api/profiles/compare?a=${a}&b=${b}`),
    enabled: !!a && !!b && a !== b,
  })
  if (!can('profiles.view')) return <NoAccess />
  const pick = (key: 'a' | 'b') => (value: string) => setParams(prev => { prev.set(key, value); return prev })
  return (
    <Panel title="Compare profiles">
      <div className="flex flex-col gap-4 px-6 py-5">
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
          {(['a', 'b'] as const).map(k => (
            <SelectField key={k} label={k === 'a' ? 'Profile' : 'With'} value={k === 'a' ? a : b} onChange={e => pick(k)(e.target.value)}>
              <option value="">Choose a profile</option>
              {profiles.data?.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            </SelectField>
          ))}
        </div>
        {result.isPending && a && b && a !== b && <Loading />}
        {result.error && <ErrorNote error={result.error} />}
        {result.data && (
          <>
            <div className="relative overflow-x-auto">
              <table className="w-full border-collapse text-left">
                <tbody>
                  {result.data.identity.map(i => (
                    <tr key={i.field} className={`border-t border-line ${i.a !== i.b ? 'bg-warn/10' : ''}`}>
                      <th scope="row" className="px-3 py-2 text-2xs font-semibold uppercase tracking-label text-text-muted">{i.field}</th>
                      <td className="px-3 py-2 font-mono text-sm">{i.a ?? '—'}</td>
                      <td className="px-3 py-2 font-mono text-sm">{i.b ?? '—'}{i.a !== i.b && <span className="sr-only"> (differs)</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <DiffView files={result.data.files} />
          </>
        )}
      </div>
    </Panel>
  )
}

export function PresetsPage() {
  const can = useCan()
  const queryClient = useQueryClient()
  const presets = useQuery({ queryKey: ['presets'], queryFn: () => api<PresetDto[]>('/api/difficulty-presets'), enabled: can('profiles.view') })
  const [editing, setEditing] = useState<{ id: string | null; name: string; values: Record<string, JsonValue> } | null>(null)
  const save = useMutation({
    mutationFn: () => editing!.id
      ? api(`/api/difficulty-presets/${editing!.id}`, { method: 'PUT', json: { name: editing!.name, values: editing!.values } })
      : api('/api/difficulty-presets', { json: { name: editing!.name, values: editing!.values } }),
    onSuccess: async () => { setEditing(null); await queryClient.invalidateQueries({ queryKey: ['presets'] }) },
  })
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/difficulty-presets/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['presets'] }),
  })
  if (!can('profiles.view')) return <NoAccess />
  const manage = can('profiles.manage')
  return (
    <Panel title="Difficulty presets" meta={manage && <Button variant="primary" onClick={() => { save.reset(); setEditing({ id: null, name: '', values: {} }) }}>New preset</Button>}>
      <div className="flex flex-col gap-3 px-6 py-5">
        <p className="m-0 text-sm text-text-muted">Recruit, Regular and Veteran are Bohemia's own. Profiles copy a preset's values, so changing one here never changes a server.</p>
        {presets.isPending && <Loading />}
        {remove.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(remove.error)}</p>}
        <ul className="m-0 flex list-none flex-col gap-2 p-0">
          {presets.data?.map(p => (
            <li key={p.id} className="flex flex-wrap items-center gap-2 border border-line px-3 py-2">
              <span className="flex-1 font-semibold">{p.name}</span>
              {p.isBuiltIn && <StatusBlock tone="off">Bohemia</StatusBlock>}
              {manage && <Button size="sm" onClick={() => { save.reset(); setEditing({ id: null, name: `${p.name} copy`.slice(0, 40), values: { ...p.values } }) }}>Copy</Button>}
              {manage && !p.isBuiltIn && <Button size="sm" onClick={() => { save.reset(); setEditing({ id: p.id, name: p.name, values: { ...p.values } }) }}>Edit</Button>}
              {manage && !p.isBuiltIn && <Button size="sm" variant="danger" busy={remove.isPending} onClick={() => remove.mutate(p.id)}>Delete</Button>}
            </li>
          ))}
        </ul>
      </div>
      <Dialog open={editing !== null} onClose={() => setEditing(null)} title={editing?.id ? 'Edit preset' : 'New preset'}>
        {editing && (
          <form className="flex flex-col gap-4" onSubmit={e => { e.preventDefault(); save.mutate() }}>
            <Field label="Name" value={editing.name} maxLength={40} required onChange={e => setEditing({ ...editing, name: e.target.value })} />
            <SettingsForm file="difficulty" tab="difficulty" values={editing.values} errors={{}}
              onChange={(k, v) => { const values = { ...editing.values }; if (v === undefined) delete values[k]; else values[k] = v; setEditing({ ...editing, values }) }} />
            {save.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(save.error)}</p>}
            <div className="flex justify-end gap-2.5">
              <Button onClick={() => setEditing(null)}>Cancel</Button>
              <Button type="submit" variant="primary" busy={save.isPending}>Save preset</Button>
            </div>
          </form>
        )}
      </Dialog>
    </Panel>
  )
}
