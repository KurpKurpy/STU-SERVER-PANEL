import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Field, TextArea } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import { formatBytes } from '../../lib/format'
import type { ModDto, ModStatus, PasteResult } from '../../lib/types'
import { JobsPanel } from './JobsPanel'
import { KeysPanel } from './KeysPanel'
import { useJobs } from './useJobs'
import { ScanChip } from '../missions/Compat'

const th = 'whitespace-nowrap px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-t border-line px-4 py-2.5'

export const modTone: Record<ModStatus, 'ok' | 'warn' | 'bad' | 'off'> = {
  NotInstalled: 'off', Queued: 'warn', Installing: 'warn', Installed: 'ok', Failed: 'bad', Missing: 'bad',
}
export const modStatusText: Record<ModStatus, string> = {
  NotInstalled: 'Not installed', Queued: 'Queued', Installing: 'Installing', Installed: 'Installed', Failed: 'Failed', Missing: 'Missing',
}

export const workshopUrl = (id: number) => `https://steamcommunity.com/sharedfiles/filedetails/?id=${id}`

export function useMods(enabled = true) {
  return useQuery({ queryKey: ['mods'], queryFn: () => api<ModDto[]>('/api/mods'), enabled })
}

export function ModsPage() {
  const can = useCan()
  const queryClient = useQueryClient()
  const mods = useMods(can('mods.view'))
  const { jobs, tails } = useJobs()
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [filter, setFilter] = useState('')
  const [removing, setRemoving] = useState<ModDto | null>(null)
  const refreshAll = () => Promise.all(['mods', 'collections', 'jobs'].map(k => queryClient.invalidateQueries({ queryKey: [k] })))
  const install = useMutation({
    mutationFn: (body: { modIds?: string[]; all?: boolean; mode: string }) => api('/api/mods/install', { json: body }),
    onSuccess: async () => { setSelected(new Set()); await refreshAll() },
  })
  const refresh = useMutation({ mutationFn: () => api<{ warnings: string[] }>('/api/mods/refresh', { method: 'POST' }), onSuccess: refreshAll })
  if (!can('mods.view')) return <NoAccess />

  const list = (mods.data ?? []).filter(m => !filter || m.name.toLowerCase().includes(filter.toLowerCase()) || String(m.workshopId ?? '').includes(filter))
  const missing = (mods.data ?? []).filter(m => m.workshopId && ['NotInstalled', 'Failed', 'Missing'].includes(m.status)).length
  const updates = (mods.data ?? []).filter(m => m.updateAvailable).length
  const unlisted = (mods.data ?? []).filter(m => m.notPublic && m.status === 'Installed').length
  const toggle = (id: string) => setSelected(s => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n })
  const workshopSelected = [...selected].filter(id => mods.data?.find(m => m.id === id)?.workshopId)

  return (
    <div className="flex flex-col gap-4">
      <JobsPanel jobs={jobs} tails={tails} types={['workshop.download', 'workshop.remove']} />
      <Panel title="Mod library" meta={
        <>
          <Link to="/mods/lists">Mod lists</Link>
          {can('mods.manage') && <Button size="sm" busy={refresh.isPending} onClick={() => refresh.mutate()}>Check for updates</Button>}
          {can('mods.install') && updates + unlisted > 0 && <Button size="sm" onClick={() => install.mutate({ all: true, mode: 'update' })}
            title={unlisted ? "Unlisted mods are fetched every time: Steam can't say when they change." : undefined}>Update all ({updates}{unlisted ? ` + ${unlisted} unlisted` : ''})</Button>}
          {can('mods.install') && missing > 0 && <Button size="sm" variant="primary" onClick={() => install.mutate({ all: true, mode: 'install' })}>Install missing ({missing})</Button>}
        </>
      }>
        <div className="flex flex-col gap-3 px-6 py-4">
          <Field label="Filter" value={filter} placeholder="Name or Workshop ID" onChange={e => setFilter(e.target.value)} className="max-w-sm" />
          {refresh.data?.warnings.map(w => <p key={w} role="status" className="m-0 text-sm text-warn">{w}</p>)}
          {(install.error || refresh.error) && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(install.error ?? refresh.error)}</p>}
          {can('mods.install') && workshopSelected.length > 0 && (
            <div className="flex flex-wrap items-center gap-2" role="toolbar" aria-label="Selected mods">
              <span className="text-sm text-text-secondary">{workshopSelected.length} selected</span>
              <Button size="sm" variant="primary" onClick={() => install.mutate({ modIds: workshopSelected, mode: 'install' })}>Download</Button>
              <Button size="sm" onClick={() => install.mutate({ modIds: workshopSelected, mode: 'reinstall' })}>Reinstall</Button>
              <Button size="sm" onClick={() => setSelected(new Set())}>Clear selection</Button>
            </div>
          )}
        </div>
        {mods.isPending && <div className="px-6 pb-5"><Loading /></div>}
        {mods.error && <div className="px-6 pb-5"><ErrorNote error={mods.error} onRetry={() => mods.refetch()} /></div>}
        {mods.data?.length === 0 && <p className="m-0 px-6 pb-5 text-text-muted">No mods yet. Paste Workshop IDs or links below, or import a Launcher preset on the Mod lists page.</p>}
        {!!list.length && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-left">
              <thead>
                <tr>
                  <th scope="col" className={th}><span className="sr-only">Select</span></th>
                  {['Mod', 'Workshop ID', 'Size', 'Status', 'Installed', 'In lists', ''].map(h => <th key={h} scope="col" className={th}>{h}</th>)}
                </tr>
              </thead>
              <tbody>
                {list.map(m => (
                  <tr key={m.id} className="hover:bg-white/[.03]">
                    <td className={td}>
                      {m.workshopId && <input type="checkbox" aria-label={`Select ${m.name}`} checked={selected.has(m.id)} onChange={() => toggle(m.id)} className="size-4" />}
                    </td>
                    <td className={`${td} font-semibold`}>
                      {m.workshopId ? <a href={workshopUrl(m.workshopId)} target="_blank" rel="noreferrer">{m.name}</a> : m.name}
                      {m.localFolder && <span className="ml-2 font-mono text-sm font-normal text-text-muted">{m.localFolder}</span>}
                    </td>
                    <td className={`${td} font-mono text-sm`}>{m.workshopId ?? 'Local'}</td>
                    <td className={`${td} whitespace-nowrap text-sm`}>{m.sizeBytes ? formatBytes(m.sizeBytes) : m.expectedBytes ? `~${formatBytes(m.expectedBytes)}` : '—'}</td>
                    <td className={td}>
                      <div className="flex flex-wrap items-center gap-1.5">
                        <StatusBlock tone={modTone[m.status]}>{modStatusText[m.status]}</StatusBlock>
                        {m.updateAvailable && <StatusBlock tone="warn">Update</StatusBlock>}
                        {m.status === 'Installed' && <ScanChip status={m.scanStatus} note={m.scanNote} />}
                      </div>
                      {m.lastError && <p className="m-0 mt-1 max-w-xs text-xs text-accent-text">{m.lastError}</p>}
                    </td>
                    <td className={`${td} whitespace-nowrap text-sm text-text-secondary`}>{m.status === 'Installed' && m.installedAt ? formatWhen(m.installedAt) : '—'}</td>
                    <td className={`${td} text-sm text-text-secondary`}>{m.lists.join(', ') || '—'}</td>
                    <td className={td}>
                      {can('mods.manage') && (
                        <div className="flex justify-end gap-2">
                          {m.status === 'Installed' && <RescanButton path={`/api/mods/${m.id}/scan`} invalidate="mods" />}
                          <Button size="sm" variant="danger" onClick={() => setRemoving(m)}>Remove</Button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        {can('mods.manage') && <AddMods />}
        <SteamAccount />
      </div>
      <KeysPanel />
      <Dialog open={removing !== null} onClose={() => setRemoving(null)} title="Remove mod">
        {removing && <RemoveMod mod={removing} onDone={() => setRemoving(null)} />}
      </Dialog>
    </div>
  )
}

function AddMods() {
  const queryClient = useQueryClient()
  const [text, setText] = useState('')
  const add = useMutation({
    mutationFn: () => api<PasteResult>('/api/mods', { json: { text } }),
    onSuccess: async () => { setText(''); await queryClient.invalidateQueries({ queryKey: ['mods'] }) },
  })
  function submit(e: FormEvent) {
    e.preventDefault()
    add.mutate()
  }
  const r = add.data
  return (
    <Panel title="Add mods">
      <form onSubmit={submit} className="flex flex-col gap-3 px-6 py-5">
        <TextArea label="Workshop IDs or links" rows={5} value={text} required placeholder={'450814997\nhttps://steamcommunity.com/sharedfiles/filedetails/?id=463939057'}
          hint="One per line. They're added as Not installed; download them from the library." className="[&_textarea]:font-mono [&_textarea]:text-sm"
          onChange={e => setText(e.target.value)} />
        <Button type="submit" variant="primary" className="self-start" busy={add.isPending}>Add to library</Button>
        {add.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(add.error)}</p>}
        {r && (
          <div role="status" className="flex flex-col gap-1 text-sm">
            <p className="m-0">Added {r.added.length}{r.existing.length ? `; ${r.existing.length} already in the library` : ''}.</p>
            {r.refused.map(x => <p key={x.workshopId} className="m-0 text-accent-text">{x.workshopId}: {x.reason}</p>)}
            {r.problems.map(p => <p key={p} className="m-0 text-warn">{p}</p>)}
          </div>
        )}
      </form>
    </Panel>
  )
}

function SteamAccount() {
  const can = useCan()
  const queryClient = useQueryClient()
  const steam = useQuery({ queryKey: ['steam'], queryFn: () => api<{ username: string | null }>('/api/steam') })
  const [username, setUsername] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: () => api('/api/steam', { method: 'PUT', json: { username: username ?? '' } }),
    onSuccess: async () => { setUsername(null); await queryClient.invalidateQueries({ queryKey: ['steam'] }) },
  })
  const value = username ?? steam.data?.username ?? ''
  function submit(e: FormEvent) {
    e.preventDefault()
    save.mutate()
  }
  return (
    <Panel title="Steam account">
      <form onSubmit={submit} className="flex flex-col gap-3 px-6 py-5">
        <p className="m-0 text-sm text-text-secondary">
          SteamCMD signs in as this account to download the server and Workshop mods; it must own Arma 3. Only the name is stored. When Steam
          asks for the password or a Steam Guard code, the job shows a box for it here, and the answer goes straight to SteamCMD.
        </p>
        <Field label="Steam account name" value={value} maxLength={64} disabled={!can('mods.install')} autoComplete="off"
          error={save.error ? errorMessage(save.error) : undefined} onChange={e => setUsername(e.target.value)} />
        {can('mods.install') && <Button type="submit" className="self-start" busy={save.isPending} disabled={username === null}>Save</Button>}
      </form>
    </Panel>
  )
}

function RemoveMod({ mod, onDone }: { mod: ModDto; onDone: () => void }) {
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: (files: boolean) => api(`/api/mods/${mod.id}${files ? '?files=true' : ''}`, { method: 'DELETE' }),
    onSuccess: async () => { await Promise.all(['mods', 'jobs'].map(k => queryClient.invalidateQueries({ queryKey: [k] }))); onDone() },
  })
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0">Remove <strong>{mod.name}</strong> from the library?</p>
      {mod.lists.length > 0 && <p className="m-0 text-warn">It's in {mod.lists.join(', ')}. Take it out of those lists first.</p>}
      {remove.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(remove.error)}</p>}
      <div className="flex flex-wrap justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button onClick={() => remove.mutate(false)} busy={remove.isPending && remove.variables === false}>Remove from the library</Button>
        {mod.workshopId && (
          <Button variant="danger" onClick={() => remove.mutate(true)} busy={remove.isPending && remove.variables === true}>Remove and delete its files</Button>
        )}
      </div>
    </div>
  )
}

// Phase 5b: read a mod's addons and maps again (after copying files into a local mod, say). The result arrives a moment later.
export function RescanButton({ path, invalidate }: { path: string; invalidate: string }) {
  const queryClient = useQueryClient()
  const rescan = useMutation({
    mutationFn: () => api(path, { method: 'POST' }),
    onSuccess: () => setTimeout(() => queryClient.invalidateQueries({ queryKey: [invalidate] }), 1500),
  })
  return (
    <Button size="sm" busy={rescan.isPending} onClick={() => rescan.mutate()} title={rescan.error ? errorMessage(rescan.error) : 'Read its addons and maps again'}>
      {rescan.isSuccess ? 'Scanning…' : rescan.isError ? 'Rescan failed' : 'Rescan'}
    </Button>
  )
}
