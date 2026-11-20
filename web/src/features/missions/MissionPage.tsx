import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { lazy, Suspense, useState, type FormEvent } from 'react'
import { Link, useParams } from 'react-router'
import { Button, buttonClasses } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Field, SelectField, TextArea } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { ApiError, api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import { formatBytes } from '../../lib/format'
import type { DeploymentStatus, FileContentDto, InstallCompat, MissionDto, VersionCompare, VersionDto } from '../../lib/types'
import { CompatChip, CompatReasons } from './Compat'
import { UploadForm } from './UploadDialog'
import { categories, useDeployments, useMission, useVersions } from './useMissions'

const CodeDiff = lazy(() => import('./CodeView').then(m => ({ default: m.CodeDiff })))

const th = 'whitespace-nowrap px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-t border-line px-4 py-2.5'
const tone: Record<DeploymentStatus, 'ok' | 'warn' | 'bad' | 'off'> = { Pending: 'warn', Waiting: 'warn', Done: 'ok', Failed: 'bad' }
const statusText: Record<DeploymentStatus, string> = { Pending: 'Sending', Waiting: 'Waiting', Done: 'On the server', Failed: 'Not written' }

export function MissionPage() {
  const { id = '' } = useParams()
  const can = useCan()
  const mission = useMission(id)
  const [uploading, setUploading] = useState(false)
  const queryClient = useQueryClient()
  const archive = useMutation({
    mutationFn: (to: boolean) => api<MissionDto>(`/api/missions/${id}/${to ? 'archive' : 'unarchive'}`, { method: 'POST' }),
    onSuccess: m => { queryClient.setQueryData(['mission', id], m); queryClient.invalidateQueries({ queryKey: ['missions'] }) },
  })
  if (!can('missions.view')) return <NoAccess />
  if (mission.isPending) return <Loading />
  if (mission.error) return <ErrorNote error={mission.error} onRetry={() => mission.refetch()} />
  const m = mission.data

  return (
    <>
      <Panel title={m.displayName} meta={
        <span className="flex flex-wrap gap-2">
          <Link to={`/missions/${id}/edit`} className={buttonClasses('primary', 'sm')}>{m.canEdit ? 'Open editor' : 'View files'}</Link>
          {m.canEdit && <Button size="sm" onClick={() => setUploading(true)}>Upload new version</Button>}
          {can('missions.download') && m.currentVersion && <a className={buttonClasses('default', 'sm')} href={`/api/missions/${id}/versions/${m.currentVersion}/pbo`}>Download PBO</a>}
          {m.canEdit && <Button size="sm" variant="danger" busy={archive.isPending} onClick={() => archive.mutate(!m.isArchived)}>{m.isArchived ? 'Unarchive' : 'Archive'}</Button>}
        </span>
      }>
        <div className="flex flex-wrap gap-x-8 gap-y-2 px-6 py-4 text-sm">
          <span><span className="text-text-muted">File </span><span className="break-all font-mono">{m.fileName}</span></span>
          <span><span className="text-text-muted">Map </span>{m.map}</span>
          <span><span className="text-text-muted">Version </span>{m.currentVersion ? `v${m.currentVersion} · ${formatWhen(m.currentVersionAt)}` : '—'}</span>
          <span><span className="text-text-muted">Author </span>{m.author?.name ?? '—'}</span>
          {m.isArchived && <StatusBlock tone="off">Archived</StatusBlock>}
        </div>
        {archive.error && <p role="alert" className="m-0 px-6 pb-4 text-sm text-accent-text">{errorMessage(archive.error)}</p>}
      </Panel>
      <div className="grid gap-6 xl:grid-cols-2">
        <Details mission={m} />
        <Deployments id={id} />
      </div>
      <ModLists id={id} />
      <Versions mission={m} />
      <Dialog open={uploading} onClose={() => setUploading(false)} title="Upload a new version">
        {uploading && <UploadForm mission={m} onDone={() => { setUploading(false); queryClient.invalidateQueries({ queryKey: ['mission', id] }) }} />}
      </Dialog>
    </>
  )
}

function Details({ mission: m }: { mission: MissionDto }) {
  const can = useCan()
  const queryClient = useQueryClient()
  const [draft, setDraft] = useState({ displayName: m.displayName, description: m.description, gameType: m.gameType ?? '', category: m.category ?? '', tags: m.tags.join(', ') })
  const [collaborators, setCollaborators] = useState(m.collaborators)
  const members = useQuery({ queryKey: ['members'], queryFn: () => api<{ id: string; userName: string; displayName: string }[]>('/api/members'), enabled: m.canShare && can('members.view') })
  const save = useMutation({
    mutationFn: () => api<MissionDto>(`/api/missions/${m.id}`, {
      method: 'PUT',
      json: { ...draft, gameType: draft.gameType || null, category: draft.category || null, tags: draft.tags.split(',').map(t => t.trim()).filter(Boolean), collaborators: collaborators.map(c => c.id) },
    }),
    onSuccess: d => { queryClient.setQueryData(['mission', m.id], d); queryClient.invalidateQueries({ queryKey: ['missions'] }) },
  })
  const fieldError = (f: string) => save.error instanceof ApiError && save.error.field === f ? save.error.message : undefined
  if (!m.canEdit)
    return (
      <Panel title="Details">
        <div className="flex flex-col gap-3 px-6 py-5 text-sm">
          <p className="m-0 whitespace-pre-wrap">{m.description || <span className="text-text-muted">No description.</span>}</p>
          <p className="m-0 text-text-secondary">{[m.category, m.gameType].filter(Boolean).join(' · ') || '—'}</p>
          {m.tags.length > 0 && <p className="m-0">{m.tags.join(', ')}</p>}
        </div>
      </Panel>
    )
  const submit = (e: FormEvent) => { e.preventDefault(); save.mutate() }
  const others = (members.data ?? []).filter(u => u.id !== m.author?.id && !collaborators.some(c => c.id === u.id))
  return (
    <Panel title="Details">
      <form onSubmit={submit} className="flex flex-col gap-4 px-6 py-5">
        <Field label="Name" value={draft.displayName} maxLength={120} required error={fieldError('displayName')} onChange={e => setDraft({ ...draft, displayName: e.target.value })} />
        <TextArea label="Description" rows={4} value={draft.description} maxLength={4000} error={fieldError('description')} onChange={e => setDraft({ ...draft, description: e.target.value })} />
        <div className="grid gap-4 sm:grid-cols-2">
          <SelectField label="Category" value={draft.category} onChange={e => setDraft({ ...draft, category: e.target.value })}>
            <option value="">None</option>
            {categories.map(c => <option key={c} value={c}>{c}</option>)}
          </SelectField>
          <Field label="Type" placeholder="Coop, TvT, Zeus…" value={draft.gameType} maxLength={40} error={fieldError('gameType')} onChange={e => setDraft({ ...draft, gameType: e.target.value })} />
        </div>
        <Field label="Tags" hint="Comma-separated, up to 10" value={draft.tags} error={fieldError('tags')} onChange={e => setDraft({ ...draft, tags: e.target.value })} />
        {!m.canShare && collaborators.length > 0 && (
          <div className="flex flex-col gap-2">
            <span className="text-2xs font-semibold uppercase tracking-label text-text-muted">Collaborators (can edit it too)</span>
            <p className="m-0 text-sm text-text-secondary">{collaborators.map(c => c.name).join(', ')}</p>
            <p className="m-0 text-xs text-text-muted">Only the author or a mission editor changes who they are.</p>
          </div>
        )}
        {m.canShare && can('members.view') && (
          <div className="flex flex-col gap-2">
            <span className="text-2xs font-semibold uppercase tracking-label text-text-muted">Collaborators (can edit it too)</span>
            <div className="flex flex-wrap gap-2">
              {collaborators.map(c => (
                <button key={c.id} type="button" onClick={() => setCollaborators(cs => cs.filter(x => x.id !== c.id))}
                  className="border border-line-strong px-2 py-1 text-xs hover:border-accent" aria-label={`Remove ${c.name}`}>{c.name} ×</button>
              ))}
              {others.length > 0 && (
                <select aria-label="Add a collaborator" className="border border-line-strong bg-surface px-2 py-1 text-xs" value=""
                  onChange={e => { const u = others.find(o => o.id === e.target.value); if (u) setCollaborators(cs => [...cs, { id: u.id, name: u.displayName || u.userName }]) }}>
                  <option value="">Add…</option>
                  {others.map(u => <option key={u.id} value={u.id}>{u.displayName || u.userName}</option>)}
                </select>
              )}
            </div>
          </div>
        )}
        {save.error && !(save.error instanceof ApiError && save.error.field) && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(save.error)}</p>}
        <div className="flex items-center justify-end gap-3">
          {save.isSuccess && !save.isPending && <span role="status" className="text-sm text-text-secondary">Saved.</span>}
          <Button type="submit" variant="primary" busy={save.isPending}>Save details</Button>
        </div>
      </form>
    </Panel>
  )
}

function Deployments({ id }: { id: string }) {
  const deployments = useDeployments(id)
  return (
    <Panel title="On the servers" meta="Every saved version is written to each server install">
      {deployments.isPending && <Loading />}
      {deployments.error && <ErrorNote error={deployments.error} onRetry={() => deployments.refetch()} />}
      {deployments.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No server install yet. Register one under Server → Setup.</p>}
      {!!deployments.data?.length && (
        <ul className="m-0 flex list-none flex-col p-0">
          {deployments.data.map(d => (
            <li key={d.installId} className="flex flex-col gap-1 border-t border-line px-6 py-3 first:border-t-0">
              <span className="flex flex-wrap items-center gap-2">
                <span className="font-semibold">{d.installName}</span>
                <StatusBlock tone={tone[d.status]}>{statusText[d.status]}</StatusBlock>
                <span className="text-sm text-text-secondary">v{d.version} · {formatWhen(d.deployedAt ?? d.createdAt)}</span>
              </span>
              {d.error && <span className="text-sm text-text-secondary">{d.error}</span>}
              {d.backupPath && <span className="break-all text-xs text-text-muted">Previous file backed up to <span className="font-mono">{d.backupPath}</span></span>}
            </li>
          ))}
        </ul>
      )}
    </Panel>
  )
}

// Phase 5b: which mod lists can run the version each server has (design call 21: compatibility replaces assigning mods by hand).
function ModLists({ id }: { id: string }) {
  const compat = useQuery({ queryKey: ['mission-compat', id], queryFn: () => api<InstallCompat[]>(`/api/missions/${id}/compatibility`) })
  return (
    <Panel title="Mod lists" meta="Which mod lists can run the version on each server">
      {compat.isPending && <Loading />}
      {compat.error && <ErrorNote error={compat.error} onRetry={() => compat.refetch()} />}
      {compat.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">This mission isn't on a server yet.</p>}
      {compat.data?.map(install => (
        <div key={install.installId} className="border-t border-line first:border-t-0">
          <p className="m-0 px-6 pt-4 text-sm text-text-secondary"><span className="font-semibold text-text">{install.installName}</span> · v{install.version}</p>
          {install.lists.length === 0 && <p className="m-0 px-6 py-3 text-sm text-text-muted">No mod lists yet.</p>}
          <ul className="m-0 flex list-none flex-col p-0 pb-2">
            {install.lists.map(l => (
              <li key={l.id} className="px-6 py-2">
                <span className="flex flex-wrap items-center gap-2"><span className="font-semibold">{l.name}</span><CompatChip status={l.status} /></span>
                <CompatReasons r={l} />
              </li>
            ))}
          </ul>
        </div>
      ))}
    </Panel>
  )
}

function Versions({ mission: m }: { mission: MissionDto }) {
  const can = useCan()
  const versions = useVersions(m.id)
  const queryClient = useQueryClient()
  const [compare, setCompare] = useState<{ a: number; b: number } | null>(null)
  const act = useMutation({
    mutationFn: ({ n, what }: { n: number; what: 'restore' | 'open' }) => api(`/api/missions/${m.id}/versions/${n}/${what}`, { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['mission', m.id] }),
  })
  const list = versions.data ?? []
  const current = list.find(v => v.current)
  return (
    <>
      {current && <Addons version={current} />}
      <Panel title="Versions" meta={list.length > 1 && <Button size="sm" onClick={() => setCompare({ a: list[1].number, b: list[0].number })}>Compare</Button>}>
        {versions.isPending && <Loading />}
        {versions.error && <ErrorNote error={versions.error} onRetry={() => versions.refetch()} />}
        {act.error && <p role="alert" className="m-0 px-6 pt-4 text-sm text-accent-text">{errorMessage(act.error)}</p>}
        {list.length > 0 && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-sm">
              <thead><tr><th className={th}>Version</th><th className={th}>Note</th><th className={th}>Saved</th><th className={th}>Size</th><th className={th}><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {list.map(v => (
                  <tr key={v.number}>
                    <td className={`${td} whitespace-nowrap`}>v{v.number} {v.current && <StatusBlock tone="ok">Current</StatusBlock>}</td>
                    <td className={td}>{v.note ?? <span className="text-text-muted">—</span>}</td>
                    <td className={`${td} whitespace-nowrap text-text-secondary`}>{formatWhen(v.createdAt)}{v.createdBy ? ` · ${v.createdBy}` : ''}</td>
                    <td className={`${td} whitespace-nowrap tabular-nums text-text-secondary`}>{formatBytes(v.pboSize)}</td>
                    <td className={`${td} whitespace-nowrap text-right`}>
                      <span className="inline-flex flex-wrap justify-end gap-2">
                        {m.canEdit && !v.current && <Button size="sm" busy={act.isPending && act.variables?.n === v.number && act.variables.what === 'restore'} onClick={() => act.mutate({ n: v.number, what: 'restore' })}>Restore</Button>}
                        {m.canEdit && <Button size="sm" onClick={() => act.mutate({ n: v.number, what: 'open' })}>Edit from here</Button>}
                        {can('missions.download') && <a className={buttonClasses('default', 'sm')} href={`/api/missions/${m.id}/versions/${v.number}/pbo`}>PBO</a>}
                        {can('missions.download') && v.hasOriginal && <a className={buttonClasses('default', 'sm')} href={`/api/missions/${m.id}/versions/${v.number}/original`}>Uploaded file</a>}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {act.isSuccess && act.variables?.what === 'open' && <p role="status" className="m-0 px-6 py-3 text-sm text-text-secondary">The working copy now holds v{act.variables.n}. Open the editor to change it, then save a version.</p>}
      </Panel>
      <Dialog open={compare !== null} onClose={() => setCompare(null)} title="Compare versions">
        {compare && <Compare id={m.id} versions={list} value={compare} onChange={setCompare} />}
      </Dialog>
    </>
  )
}

function Addons({ version }: { version: VersionDto }) {
  const groups = new Map<string, string[]>()
  for (const a of version.addons) {
    const key = a.modName ?? 'Mod not recorded by Eden'
    groups.set(key, [...(groups.get(key) ?? []), a.addon])
  }
  return (
    <Panel title="Required addons" meta={`v${version.number}${version.binarized ? ' · binarized mission.sqm' : ''}`}>
      {version.addons.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">This mission lists no addons.</p>}
      <ul className="m-0 grid list-none gap-x-8 gap-y-3 p-0 px-6 py-5 text-sm md:grid-cols-2">
        {[...groups].sort(([a], [b]) => a.localeCompare(b)).map(([mod, addons]) => (
          <li key={mod} className="min-w-0">
            <span className="font-semibold">{mod}</span>
            <span className="block break-words font-mono text-xs text-text-muted">{addons.join(', ')}</span>
          </li>
        ))}
      </ul>
    </Panel>
  )
}

function Compare({ id, versions, value, onChange }: { id: string; versions: VersionDto[]; value: { a: number; b: number }; onChange: (v: { a: number; b: number }) => void }) {
  const diff = useQuery({ queryKey: ['mission', id, 'compare', value.a, value.b], queryFn: () => api<VersionCompare>(`/api/missions/${id}/versions/compare?a=${value.a}&b=${value.b}`) })
  const [path, setPath] = useState<string | null>(null)
  const contents = useQuery({
    queryKey: ['mission', id, 'compare-file', value.a, value.b, path], enabled: path !== null,
    queryFn: () => Promise.all([value.a, value.b].map(n => api<FileContentDto>(`/api/missions/${id}/versions/${n}/file?path=${encodeURIComponent(path!)}`))),
  })
  const pickVersion = (key: 'a' | 'b') => (
    <SelectField label={key === 'a' ? 'From' : 'To'} value={value[key]} onChange={e => { setPath(null); onChange({ ...value, [key]: Number(e.target.value) }) }}>
      {versions.map(v => <option key={v.number} value={v.number}>v{v.number}</option>)}
    </SelectField>
  )
  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3">{pickVersion('a')}{pickVersion('b')}</div>
      {diff.isPending && <Loading />}
      {diff.data && (
        <ul className="m-0 flex max-h-56 list-none flex-col gap-1 overflow-y-auto p-0 text-sm">
          {diff.data.changed.map(c => (
            <li key={c.path}><button type="button" className={`break-all text-left font-mono hover:text-accent-text ${path === c.path ? 'text-accent-text' : ''}`} onClick={() => setPath(c.path)}>~ {c.path}</button></li>
          ))}
          {diff.data.added.map(p => <li key={p} className="break-all font-mono text-text-secondary">+ {p}</li>)}
          {diff.data.removed.map(p => <li key={p} className="break-all font-mono text-text-secondary">− {p}</li>)}
          {!diff.data.changed.length && !diff.data.added.length && !diff.data.removed.length && <li className="text-text-muted">The two versions have the same files.</li>}
        </ul>
      )}
      {contents.data && (contents.data.some(c => c.text === null)
        ? <p className="m-0 text-sm text-text-muted">This file isn&apos;t text, so it can&apos;t be shown side by side.</p>
        : <Suspense fallback={<Loading />}><CodeDiff path={path!} original={contents.data[0].text ?? ''} modified={contents.data[1].text ?? ''} height={360} /></Suspense>)}
    </div>
  )
}
