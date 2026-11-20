import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Field, TextArea } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { CollectionDto, CollectionSummary, ModDto } from '../../lib/types'

const th = 'whitespace-nowrap px-6 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-t border-line px-6 py-3'

export function useCollections(enabled = true) {
  return useQuery({ queryKey: ['collections'], queryFn: () => api<CollectionSummary[]>('/api/collections'), enabled })
}

export function CollectionsPage() {
  const can = useCan()
  const lists = useCollections(can('mods.view'))
  const [dialog, setDialog] = useState<{ kind: 'new' | 'import' } | { kind: 'duplicate' | 'delete'; list: CollectionSummary } | null>(null)
  if (!can('mods.view')) return <NoAccess />
  const manage = can('collections.manage')
  const close = () => setDialog(null)

  return (
    <div className="flex flex-col gap-4">
      <Panel title="Mod lists" meta={
        <>
          <Link to="/mods">Mod library</Link>
          {manage && <Button size="sm" onClick={() => setDialog({ kind: 'import' })}>Import preset</Button>}
          {manage && <Button size="sm" variant="primary" onClick={() => setDialog({ kind: 'new' })}>New list</Button>}
        </>
      }>
        <p className="m-0 px-6 pt-4 text-sm text-text-secondary">
          A profile picks a mod list on its Mods tab. Client mods load with <span className="font-mono">-mod</span>, server-only mods with{' '}
          <span className="font-mono">-serverMod</span>, and headless clients load the Headless ones. Changes apply at the next Start.
        </p>
        {lists.isPending && <div className="px-6 py-5"><Loading /></div>}
        {lists.error && <div className="px-6 py-5"><ErrorNote error={lists.error} onRetry={() => lists.refetch()} /></div>}
        {lists.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No mod lists yet. Import an Arma 3 Launcher preset, or start a new list.</p>}
        {!!lists.data?.length && (
          <div className="relative mt-3 overflow-x-auto">
            <table className="w-full border-collapse text-left">
              <thead><tr>{['List', 'Mods', 'Used by', 'Changed', ''].map(h => <th key={h} scope="col" className={th}>{h}</th>)}</tr></thead>
              <tbody>
                {lists.data.map(l => (
                  <tr key={l.id} className="hover:bg-white/[.03]">
                    <td className={td}>
                      <Link to={`/mods/lists/${l.id}`} className="font-semibold">{l.name}</Link>
                      {l.description && <p className="m-0 text-sm text-text-muted">{l.description}</p>}
                    </td>
                    <td className={td}>
                      <span className="font-mono">{l.count}</span>
                      {l.notInstalled > 0 && <span className="ml-2"><StatusBlock tone="warn">{l.notInstalled} not installed</StatusBlock></span>}
                    </td>
                    <td className={`${td} text-sm text-text-secondary`}>{l.usedBy.join(', ') || '—'}</td>
                    <td className={`${td} whitespace-nowrap text-sm text-text-secondary`}>{formatWhen(l.updatedAt)}</td>
                    <td className={td}>
                      <div className="flex justify-end gap-2">
                        <a href={`/api/collections/${l.id}/export`} download className="inline-flex min-h-9 items-center px-2 text-sm">Export</a>
                        {manage && <Button size="sm" onClick={() => setDialog({ kind: 'duplicate', list: l })}>Duplicate</Button>}
                        {manage && <Button size="sm" variant="danger" onClick={() => setDialog({ kind: 'delete', list: l })}>Delete</Button>}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <Dialog open={dialog?.kind === 'new'} onClose={close} title="New mod list">{dialog?.kind === 'new' && <NewList onDone={close} />}</Dialog>
      <Dialog open={dialog?.kind === 'import'} onClose={close} title="Import a preset">{dialog?.kind === 'import' && <ImportPreset onDone={close} />}</Dialog>
      <Dialog open={dialog?.kind === 'duplicate'} onClose={close} title="Duplicate mod list">
        {dialog?.kind === 'duplicate' && <DuplicateList list={dialog.list} onDone={close} />}
      </Dialog>
      <Dialog open={dialog?.kind === 'delete'} onClose={close} title="Delete mod list">
        {dialog?.kind === 'delete' && <DeleteList list={dialog.list} onDone={close} />}
      </Dialog>
    </div>
  )
}

function NewList({ onDone }: { onDone: () => void }) {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [v, setV] = useState({ name: '', description: '' })
  const create = useMutation({
    mutationFn: () => api<CollectionDto>('/api/collections', { json: { ...v, items: [] } }),
    onSuccess: async list => { await queryClient.invalidateQueries({ queryKey: ['collections'] }); onDone(); navigate(`/mods/lists/${list.id}`) },
  })
  function submit(e: FormEvent) {
    e.preventDefault()
    create.mutate()
  }
  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <Field label="Name" value={v.name} maxLength={60} required placeholder="Main Unit Modset" onChange={e => setV(p => ({ ...p, name: e.target.value }))} />
      <Field label="Description" value={v.description} maxLength={500} onChange={e => setV(p => ({ ...p, description: e.target.value }))} />
      {create.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(create.error)}</p>}
      <div className="flex justify-end gap-2.5"><Button onClick={onDone}>Cancel</Button><Button type="submit" variant="primary" busy={create.isPending}>Create</Button></div>
    </form>
  )
}

interface ImportResult { collection: CollectionDto; missing: ModDto[]; problems: string[] }

function ImportPreset({ onDone }: { onDone: () => void }) {
  const can = useCan()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [text, setText] = useState('')
  const [name, setName] = useState('')
  const [fileName, setFileName] = useState<string | null>(null)
  const importIt = useMutation({
    mutationFn: () => api<ImportResult>('/api/collections/import', { json: { name: name.trim() || null, text } }),
    onSuccess: () => Promise.all(['collections', 'mods'].map(k => queryClient.invalidateQueries({ queryKey: [k] }))),
  })
  const install = useMutation({
    mutationFn: (ids: string[]) => api('/api/mods/install', { json: { modIds: ids, mode: 'install' } }),
    onSuccess: () => { onDone(); navigate('/mods') },
  })
  async function pick(file: File | undefined) {
    if (!file) return
    if (file.size > 512 * 1024) { setFileName(`${file.name} is over 512 KB`); return }
    setFileName(file.name)
    setText(await file.text())
  }
  function submit(e: FormEvent) {
    e.preventDefault()
    importIt.mutate()
  }
  const r = importIt.data
  if (r)
    return (
      <div className="flex flex-col gap-4">
        <p className="m-0">Created <strong>{r.collection.name}</strong> with {r.collection.items.length} mods.</p>
        {r.missing.length > 0 && <p className="m-0 text-warn">{r.missing.length} of them aren't installed yet.</p>}
        {r.problems.map(p => <p key={p} className="m-0 text-sm text-warn">{p}</p>)}
        {install.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(install.error)}</p>}
        <div className="flex flex-wrap justify-end gap-2.5">
          <Button onClick={() => { onDone(); navigate(`/mods/lists/${r.collection.id}`) }}>Open the list</Button>
          {r.missing.length > 0 && can('mods.install') && (
            <Button variant="primary" busy={install.isPending} onClick={() => install.mutate(r.missing.filter(m => m.workshopId).map(m => m.id))}>
              Download the {r.missing.length} missing
            </Button>
          )}
        </div>
      </div>
    )
  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <label className="flex flex-col gap-1.5">
        <span className="text-2xs font-semibold uppercase tracking-label text-text-muted">Launcher preset (.html)</span>
        <input type="file" accept=".html,.htm,text/html" onChange={e => void pick(e.target.files?.[0])} className="text-sm" />
        {fileName && <span className="text-sm text-text-muted">{fileName}</span>}
      </label>
      <TextArea label="Or paste the preset, or Workshop IDs and links" rows={6} value={text} required
        hint="In the Arma 3 Launcher: MODS, then PRESET, then EXPORT to file." className="[&_textarea]:font-mono [&_textarea]:text-xs"
        onChange={e => setText(e.target.value)} />
      <Field label="Name" value={name} maxLength={60} placeholder="The preset's own name" onChange={e => setName(e.target.value)} />
      {importIt.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(importIt.error)}</p>}
      <div className="flex justify-end gap-2.5"><Button onClick={onDone}>Cancel</Button><Button type="submit" variant="primary" busy={importIt.isPending}>Import</Button></div>
    </form>
  )
}

function DuplicateList({ list, onDone }: { list: CollectionSummary; onDone: () => void }) {
  const queryClient = useQueryClient()
  const [name, setName] = useState(`${list.name} copy`)
  const dup = useMutation({
    mutationFn: () => api(`/api/collections/${list.id}/duplicate`, { json: { name } }),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['collections'] }); onDone() },
  })
  return (
    <form onSubmit={e => { e.preventDefault(); dup.mutate() }} className="flex flex-col gap-4">
      <Field label="Name" value={name} maxLength={60} required onChange={e => setName(e.target.value)} />
      {dup.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(dup.error)}</p>}
      <div className="flex justify-end gap-2.5"><Button onClick={onDone}>Cancel</Button><Button type="submit" variant="primary" busy={dup.isPending}>Duplicate</Button></div>
    </form>
  )
}

function DeleteList({ list, onDone }: { list: CollectionSummary; onDone: () => void }) {
  const queryClient = useQueryClient()
  const del = useMutation({
    mutationFn: () => api(`/api/collections/${list.id}`, { method: 'DELETE' }),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['collections'] }); onDone() },
  })
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0">Delete <strong>{list.name}</strong>? The mods stay in the library.</p>
      {list.usedBy.length > 0 && <p className="m-0 text-warn">Used by {list.usedBy.join(', ')}: pick another list for them first.</p>}
      {del.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(del.error)}</p>}
      <div className="flex justify-end gap-2.5"><Button onClick={onDone}>Cancel</Button><Button variant="danger" busy={del.isPending} onClick={() => del.mutate()}>Delete</Button></div>
    </div>
  )
}
