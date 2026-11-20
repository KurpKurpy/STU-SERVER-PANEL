import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, NoAccess } from '../../components/Feedback'
import { Field, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { ApiError, api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { InstallDto, ProfileSummary } from '../../lib/types'
import { appliedState } from './ProfileLayout'
import { executables } from './ProfileTabs'

const slugify = (name: string) => name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 32)
const refresh = (qc: ReturnType<typeof useQueryClient>) => Promise.all(['profiles', 'servers'].map(k => qc.invalidateQueries({ queryKey: [k] })))

export function ProfilesPage() {
  const can = useCan()
  const profiles = useQuery({ queryKey: ['profiles'], queryFn: () => api<ProfileSummary[]>('/api/profiles'), enabled: can('profiles.view') })
  const [dialog, setDialog] = useState<{ kind: 'new' } | { kind: 'duplicate' | 'delete'; profile: ProfileSummary } | null>(null)
  if (!can('profiles.view')) return <NoAccess />
  const manage = can('profiles.manage')

  return (
    <div className="flex flex-col gap-4">
      <Panel title="Server profiles" meta={
        <>
          <Link to="/profiles/presets">Difficulty presets</Link>
          {(profiles.data?.length ?? 0) > 1 && <Link to={`/profiles/compare?a=${profiles.data![0].id}&b=${profiles.data![1].id}`}>Compare</Link>}
          {manage && <Button variant="primary" onClick={() => setDialog({ kind: 'new' })}>New profile</Button>}
        </>
      }>
        {profiles.isPending && <div className="px-6 py-5"><Loading /></div>}
        {profiles.error && <div className="px-6 py-5"><ErrorNote error={profiles.error} onRetry={() => profiles.refetch()} /></div>}
        {profiles.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No profiles yet.</p>}
        {!!profiles.data?.length && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-left">
              <thead>
                <tr className="text-2xs font-semibold uppercase tracking-label text-text-muted">
                  {['Profile', 'Install', 'Port', 'Revision', 'On the server', ''].map(h => <th key={h} scope="col" className="px-6 py-3 font-semibold">{h}</th>)}
                </tr>
              </thead>
              <tbody>
                {profiles.data.map(p => {
                  const state = appliedState(p)
                  return (
                    <tr key={p.id} className="border-t border-line hover:bg-white/[.03]">
                      <td className="px-6 py-3"><Link to={`/profiles/${p.id}`} className="font-semibold">{p.name}</Link> <span className="font-mono text-sm text-text-muted">{p.slug}</span></td>
                      <td className="px-6 py-3 text-text-secondary">{p.installName}</td>
                      <td className="px-6 py-3 font-mono">{p.port}</td>
                      <td className="px-6 py-3 font-mono">r{p.revision}</td>
                      <td className="px-6 py-3"><StatusBlock tone={state.tone}>{state.label}</StatusBlock></td>
                      <td className="px-6 py-3">
                        {manage && (
                          <div className="flex justify-end gap-2">
                            <Button size="sm" onClick={() => setDialog({ kind: 'duplicate', profile: p })}>Duplicate</Button>
                            <Button size="sm" variant="danger" onClick={() => setDialog({ kind: 'delete', profile: p })}>Delete</Button>
                          </div>
                        )}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <Dialog open={dialog?.kind === 'new'} onClose={() => setDialog(null)} title="New profile">
        {dialog?.kind === 'new' && <NewProfile onDone={() => setDialog(null)} />}
      </Dialog>
      <Dialog open={dialog?.kind === 'duplicate'} onClose={() => setDialog(null)} title="Duplicate profile">
        {dialog?.kind === 'duplicate' && <DuplicateProfile source={dialog.profile} onDone={() => setDialog(null)} />}
      </Dialog>
      <Dialog open={dialog?.kind === 'delete'} onClose={() => setDialog(null)} title="Delete profile">
        {dialog?.kind === 'delete' && <DeleteProfile profile={dialog.profile} onDone={() => setDialog(null)} />}
      </Dialog>
    </div>
  )
}

function useFieldError(error: unknown) {
  return (field: string) => (error instanceof ApiError && error.field === field ? error.message : undefined)
}

function NewProfile({ onDone }: { onDone: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const installs = useQuery({ queryKey: ['installs'], queryFn: () => api<InstallDto[]>('/api/installs') })
  const [v, setV] = useState({ name: '', slug: '', serverInstallId: '', executable: executables[0], port: '2302', publicAddress: '' })
  const [slugTouched, setSlugTouched] = useState(false)
  const create = useMutation({
    mutationFn: () => api<ProfileSummary>('/api/profiles', {
      json: { ...v, serverInstallId: v.serverInstallId || installs.data?.[0]?.id, port: Number(v.port), publicAddress: v.publicAddress.trim() || null },
    }),
    onSuccess: async p => { await refresh(queryClient); onDone(); navigate(`/profiles/${p.id}/general`) },
  })
  const err = useFieldError(create.error)
  if (installs.isPending) return <Loading />
  if (!installs.data?.length) return <p className="m-0">Register a server install first, in <Link to="/server/setup">Server → Setup</Link>.</p>
  function submit(e: FormEvent) { e.preventDefault(); create.mutate() }
  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <Field label="Name" value={v.name} maxLength={40} required error={err('name')}
        onChange={e => setV(p => ({ ...p, name: e.target.value, slug: slugTouched ? p.slug : slugify(e.target.value) }))} />
      <Field label="Slug" value={v.slug} maxLength={32} required error={err('slug')} hint="Names the profile's folders on the server. It can't change later."
        onChange={e => { setSlugTouched(true); setV(p => ({ ...p, slug: e.target.value })) }} />
      <SelectField label="Server install" value={v.serverInstallId || installs.data[0].id} onChange={e => setV(p => ({ ...p, serverInstallId: e.target.value }))}>
        {installs.data.map(i => <option key={i.id} value={i.id}>{i.name} ({i.installPath})</option>)}
      </SelectField>
      <SelectField label="Executable" value={v.executable} onChange={e => setV(p => ({ ...p, executable: e.target.value }))}>
        {executables.map(x => <option key={x} value={x}>{x}</option>)}
      </SelectField>
      <Field label="Game port" type="number" min={1024} max={65530} required value={v.port} error={err('port')}
        hint={`Uses ports ${v.port}–${Number(v.port) + 4}.`} onChange={e => setV(p => ({ ...p, port: e.target.value }))} />
      <Field label="Public address" value={v.publicAddress} maxLength={253} error={err('publicAddress')} onChange={e => setV(p => ({ ...p, publicAddress: e.target.value }))} />
      {create.error && !(create.error instanceof ApiError && create.error.field) && <p role="alert" className="m-0 text-accent-text">{errorMessage(create.error)}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant="primary" busy={create.isPending}>Create profile</Button>
      </div>
    </form>
  )
}

function DuplicateProfile({ source, onDone }: { source: ProfileSummary; onDone: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [name, setName] = useState(`${source.name} copy`.slice(0, 40))
  const [slug, setSlug] = useState(slugify(`${source.slug}-copy`))
  const dup = useMutation({
    mutationFn: () => api<ProfileSummary>(`/api/profiles/${source.id}/duplicate`, { json: { name, slug } }),
    onSuccess: async p => { await refresh(queryClient); onDone(); navigate(`/profiles/${p.id}`) },
  })
  const err = useFieldError(dup.error)
  return (
    <form onSubmit={e => { e.preventDefault(); dup.mutate() }} className="flex flex-col gap-4">
      <p className="m-0 text-sm text-text-muted">Copies {source.name}'s settings and passwords. The copy gets the next free block of five ports and isn't applied.</p>
      <Field label="Name" value={name} maxLength={40} required error={err('name')} onChange={e => setName(e.target.value)} />
      <Field label="Slug" value={slug} maxLength={32} required error={err('slug')} onChange={e => setSlug(e.target.value)} />
      {dup.error && !(dup.error instanceof ApiError && dup.error.field) && <p role="alert" className="m-0 text-accent-text">{errorMessage(dup.error)}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant="primary" busy={dup.isPending}>Duplicate</Button>
      </div>
    </form>
  )
}

function DeleteProfile({ profile, onDone }: { profile: ProfileSummary; onDone: () => void }) {
  const queryClient = useQueryClient()
  const del = useMutation({
    mutationFn: () => api(`/api/profiles/${profile.id}`, { method: 'DELETE' }),
    onSuccess: async () => { await refresh(queryClient); onDone() },
  })
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0">Delete {profile.name} and all its revisions? The files already on the server stay where they are.</p>
      {del.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(del.error)}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="danger" busy={del.isPending} onClick={() => del.mutate()}>Delete profile</Button>
      </div>
    </div>
  )
}
