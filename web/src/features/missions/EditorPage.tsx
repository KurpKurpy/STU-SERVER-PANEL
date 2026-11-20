import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useBlocker, useParams } from 'react-router'
import { Button, buttonClasses } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Check, Field } from '../../components/Field'
import { ApiError, api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import { formatBytes } from '../../lib/format'
import type { FileContentDto, MissionFileDto, RevisionDto, SearchHit } from '../../lib/types'
import { CodeDiff, CodeEditor } from './CodeView'
import { pickedFiles, upload, useFiles, useMission } from './useMissions'

interface Tab { path: string; sha: string | null; original: string; draft: string; readOnly: boolean; binarized: boolean; binary: boolean; size: number }
type Folder = { name: string; path: string; folders: Map<string, Folder>; files: MissionFileDto[] }

function buildTree(files: MissionFileDto[]): Folder {
  const root: Folder = { name: '', path: '', folders: new Map(), files: [] }
  for (const f of files) {
    const parts = f.path.split('/')
    let node = root
    parts.slice(0, -1).forEach((part, i) => {
      const key = part.toLowerCase()
      if (!node.folders.has(key)) node.folders.set(key, { name: part, path: parts.slice(0, i + 1).join('/'), folders: new Map(), files: [] })
      node = node.folders.get(key)!
    })
    node.files.push(f)
  }
  return root
}

const tabFrom = (c: FileContentDto): Tab => ({
  path: c.path, sha: c.sha, original: c.text ?? '', draft: c.text ?? '', readOnly: c.readOnly, binarized: c.binarized, binary: c.binary, size: c.size,
})

export function EditorPage() {
  const { id = '' } = useParams()
  const can = useCan()
  const mission = useMission(id)
  const files = useFiles(id)
  const queryClient = useQueryClient()
  const [tabs, setTabs] = useState<Tab[]>([])
  const [active, setActive] = useState<string | null>(null)
  const [line, setLine] = useState<number | undefined>()
  const [selected, setSelected] = useState<string>('') // a file or folder in the tree
  const [open, setOpen] = useState<Set<string>>(new Set())
  const [drawer, setDrawer] = useState(false)
  const [dialog, setDialog] = useState<null | 'new' | 'rename' | 'delete' | 'history' | 'version' | 'search'>(null)
  const [error, setError] = useState<string | null>(null)
  const [conflict, setConflict] = useState<string | null>(null)
  const uploadInput = useRef<HTMLInputElement>(null)
  const canEdit = mission.data?.canEdit ?? false
  const dirty = tabs.some(t => t.draft !== t.original)
  const tab = tabs.find(t => t.path === active)
  const tree = useMemo(() => buildTree(files.data ?? []), [files.data])

  // Unsaved changes are never lost silently: the browser asks before leaving, and so does in-app navigation.
  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => { if (dirty) e.preventDefault() }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])
  const blocker = useBlocker(dirty)

  async function openFile(path: string, at?: number) {
    setError(null)
    setDrawer(false)
    setSelected(path)
    setLine(at)
    if (tabs.some(t => t.path === path)) { setActive(path); return }
    try {
      const c = await api<FileContentDto>(`/api/missions/${id}/files/content?path=${encodeURIComponent(path)}`)
      setTabs(ts => [...ts, tabFrom(c)])
      setActive(c.path)
    } catch (e) { setError(errorMessage(e)) }
  }
  const closeTab = (path: string) => {
    const t = tabs.find(x => x.path === path)
    if (t && t.draft !== t.original && !window.confirm(`Close ${path} without saving?`)) return
    const rest = tabs.filter(x => x.path !== path)
    setTabs(rest)
    if (active === path) setActive(rest.at(-1)?.path ?? null)
  }
  const refreshFiles = () => queryClient.invalidateQueries({ queryKey: ['mission', id, 'files'] })

  const save = useMutation({
    mutationFn: (t: Tab) => api<FileContentDto>(`/api/missions/${id}/files/content`, { method: 'PUT', json: { path: t.path, content: t.draft, baseSha: t.sha } }),
    onSuccess: (c, t) => { setTabs(ts => ts.map(x => (x.path === t.path ? { ...x, sha: c.sha, original: t.draft, size: c.size } : x))); setConflict(null); refreshFiles() },
    onError: (e, t) => { if (e instanceof ApiError && e.code === 'stale') setConflict(t.path) },
  })
  const saveActive = () => { if (tab && !tab.readOnly && canEdit && tab.draft !== tab.original) { if (window.confirm(`Save ${tab.path}?`)) save.mutate(tab) } }
  async function reload(path: string) {
    const c = await api<FileContentDto>(`/api/missions/${id}/files/content?path=${encodeURIComponent(path)}`)
    setTabs(ts => ts.map(x => (x.path === path ? tabFrom(c) : x)))
    setConflict(null)
  }
  const convert = useMutation({
    mutationFn: (path: string) => api<FileContentDto>(`/api/missions/${id}/files/convert?path=${encodeURIComponent(path)}`, { method: 'POST' }),
    onSuccess: c => { setTabs(ts => ts.map(x => (x.path === c.path ? tabFrom(c) : x))); refreshFiles() },
  })
  async function uploadInto(list: FileList) {
    const folder = (files.data ?? []).some(f => f.path === selected) ? selected.split('/').slice(0, -1).join('/') : selected
    setError(null)
    try { await upload(`/api/missions/${id}/files/upload?folder=${encodeURIComponent(folder)}`, pickedFiles(list), () => {}); refreshFiles() }
    catch (e) { setError(errorMessage(e)) }
  }

  if (!can('missions.view')) return <NoAccess />
  if (mission.isPending || files.isPending) return <Loading />
  if (mission.error || files.error) return <ErrorNote error={mission.error ?? files.error} onRetry={() => { mission.refetch(); files.refetch() }} />
  const m = mission.data

  const Tree = ({ node, depth }: { node: Folder; depth: number }) => (
    <ul className="m-0 list-none p-0" role={depth === 0 ? 'tree' : 'group'} aria-label={depth === 0 ? 'Mission files' : undefined}>
      {[...node.folders.values()].sort((a, b) => a.name.localeCompare(b.name)).map(f => (
        <li key={f.path} role="treeitem" aria-expanded={open.has(f.path)} aria-selected={selected === f.path}>
          <button type="button" style={{ paddingLeft: 8 + depth * 14 }}
            className={`flex w-full items-center gap-1.5 py-1 pr-2 text-left text-sm ${selected === f.path ? 'bg-white/[.06] text-text' : 'text-text-secondary hover:text-text'}`}
            onClick={() => { setSelected(f.path); setOpen(s => { const n = new Set(s); if (n.has(f.path)) n.delete(f.path); else n.add(f.path); return n }) }}>
            <span aria-hidden className="w-3 text-text-muted">{open.has(f.path) ? '▾' : '▸'}</span>{f.name}
          </button>
          {open.has(f.path) && <Tree node={f} depth={depth + 1} />}
        </li>
      ))}
      {[...node.files].sort((a, b) => a.path.localeCompare(b.path)).map(f => {
        const t = tabs.find(x => x.path === f.path)
        return (
          <li key={f.path} role="treeitem" aria-selected={selected === f.path}>
            <button type="button" style={{ paddingLeft: 20 + depth * 14 }} onClick={() => openFile(f.path)}
              className={`flex w-full items-center gap-1.5 py-1 pr-2 text-left font-mono text-xs ${active === f.path ? 'bg-accent/15 text-text' : selected === f.path ? 'bg-white/[.06] text-text' : 'text-text-secondary hover:text-text'}`}>
              <span className="truncate">{f.path.split('/').at(-1)}</span>
              {t && t.draft !== t.original && <span aria-label="unsaved" className="text-accent-text">●</span>}
              {f.binarized && <span className="ml-auto text-2xs text-text-muted">binarized</span>}
            </button>
          </li>
        )
      })}
    </ul>
  )

  return (
    <div className="flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-2">
        <Link to={`/missions/${id}`} className="text-sm text-text-secondary hover:text-text">← {m.displayName}</Link>
        <span className="break-all font-mono text-xs text-text-muted">{m.fileName}</span>
        <span className="ml-auto flex flex-wrap gap-2">
          <Button size="sm" className="md:hidden" onClick={() => setDrawer(d => !d)} aria-expanded={drawer}>Files</Button>
          <Button size="sm" onClick={() => setDialog('search')}>Search</Button>
          {can('missions.download') && <a className={buttonClasses('default', 'sm')} href={`/api/missions/${id}/files/zip`}>Download .zip</a>}
          {canEdit && <Button size="sm" variant="primary" onClick={() => setDialog('version')}>Save version</Button>}
        </span>
      </div>
      {!canEdit && <p className="m-0 text-sm text-text-secondary">You can read these files. Editing needs the mission&apos;s author, a collaborator, or a mission editor.</p>}
      {error && <p role="alert" className="m-0 text-sm text-accent-text">{error}</p>}
      <div className="flex min-h-[70vh] border border-line md:h-[calc(100vh-13rem)]">
        <aside className={`${drawer ? 'flex' : 'hidden'} w-full shrink-0 flex-col border-r border-line md:flex md:w-64`} aria-label="Files">
          {canEdit && (
            <div className="flex flex-wrap gap-1.5 border-b border-line p-2">
              <Button size="sm" onClick={() => setDialog('new')}>New file</Button>
              <Button size="sm" disabled={!selected} onClick={() => setDialog('rename')}>Rename</Button>
              <Button size="sm" variant="danger" disabled={!selected} onClick={() => setDialog('delete')}>Delete</Button>
              <Button size="sm" onClick={() => uploadInput.current?.click()}>Upload</Button>
              <input ref={uploadInput} type="file" multiple hidden onChange={e => { if (e.target.files) uploadInto(e.target.files); e.target.value = '' }} />
            </div>
          )}
          <div className="flex-1 overflow-y-auto py-1"><Tree node={tree} depth={0} /></div>
        </aside>
        <section className={`${drawer ? 'hidden' : 'flex'} min-w-0 flex-1 flex-col md:flex`} aria-label="Editor">
          {tabs.length > 0 && (
            <div className="flex overflow-x-auto border-b border-line" role="tablist">
              {tabs.map(t => (
                <div key={t.path} role="tab" aria-selected={t.path === active} className={`flex shrink-0 items-center border-r border-line ${t.path === active ? 'bg-surface-raised' : ''}`}>
                  <button type="button" className="px-3 py-2 font-mono text-xs" onClick={() => setActive(t.path)}>
                    {t.path.split('/').at(-1)}{t.draft !== t.original && <span className="text-accent-text"> ●</span>}
                  </button>
                  <button type="button" aria-label={`Close ${t.path}`} className="px-2 text-text-muted hover:text-text" onClick={() => closeTab(t.path)}>×</button>
                </div>
              ))}
            </div>
          )}
          {!tab && <p className="m-auto px-6 text-center text-text-muted">Pick a file from the file list.</p>}
          {tab && (
            <>
              <div className="flex flex-wrap items-center gap-2 border-b border-line px-3 py-2 text-xs">
                <span className="break-all font-mono text-text-secondary">{tab.path}</span>
                <span className="text-text-muted">{formatBytes(tab.size)}</span>
                {tab.binarized && <span className="text-text-muted">· binarized: shown as text, read-only</span>}
                <span className="ml-auto flex flex-wrap gap-2">
                  {canEdit && tab.binarized && <Button size="sm" busy={convert.isPending} onClick={() => { if (window.confirm('Convert mission.sqm to text? The binarized original stays in the file history, and Eden and Arma read the text file the same way.')) convert.mutate(tab.path) }}>Convert to text</Button>}
                  <Button size="sm" onClick={() => setDialog('history')}>History</Button>
                  {can('missions.download') && <a className={buttonClasses('default', 'sm')} href={`/api/missions/${id}/files/download?path=${encodeURIComponent(tab.path)}`}>Download</a>}
                  {canEdit && !tab.readOnly && <Button size="sm" variant="primary" disabled={tab.draft === tab.original} busy={save.isPending} onClick={saveActive}>Save</Button>}
                </span>
              </div>
              {conflict === tab.path && (
                <p role="alert" className="m-0 flex flex-wrap items-center gap-2 border-b border-line px-3 py-2 text-sm text-accent-text">
                  Someone saved this file meanwhile. Your text is still here; reloading shows theirs and drops yours.
                  <Button size="sm" onClick={() => reload(tab.path)}>Reload theirs</Button>
                </p>
              )}
              {save.error && !(save.error instanceof ApiError && save.error.code === 'stale') && <p role="alert" className="m-0 px-3 py-2 text-sm text-accent-text">{errorMessage(save.error)}</p>}
              {convert.error && <p role="alert" className="m-0 px-3 py-2 text-sm text-accent-text">{errorMessage(convert.error)}</p>}
              <div className="min-h-0 flex-1">
                {tab.binary
                  ? <p className="m-6 text-text-muted">This file isn&apos;t text. Download it, or upload a replacement with the same name.</p>
                  : <CodeEditor path={tab.path} value={tab.draft} readOnly={tab.readOnly || !canEdit} line={line} onSave={saveActive}
                      onChange={v => setTabs(ts => ts.map(x => (x.path === tab.path ? { ...x, draft: v } : x)))} />}
              </div>
            </>
          )}
        </section>
      </div>

      <Dialog open={dialog === 'new'} onClose={() => setDialog(null)} title="New file">
        {dialog === 'new' && <NewFile id={id} folder={(files.data ?? []).some(f => f.path === selected) ? selected.split('/').slice(0, -1).join('/') : selected}
          onDone={path => { setDialog(null); refreshFiles(); openFile(path) }} />}
      </Dialog>
      <Dialog open={dialog === 'rename'} onClose={() => setDialog(null)} title="Rename">
        {dialog === 'rename' && <Rename id={id} from={selected} blocked={tabs.some(t => (t.path === selected || t.path.startsWith(selected + '/')) && t.draft !== t.original)}
          onDone={() => { setTabs(ts => ts.filter(t => t.path !== selected && !t.path.startsWith(selected + '/'))); setActive(null); setSelected(''); setDialog(null); refreshFiles() }} />}
      </Dialog>
      <Dialog open={dialog === 'delete'} onClose={() => setDialog(null)} title="Delete">
        {dialog === 'delete' && <Delete id={id} path={selected}
          onDone={() => { setTabs(ts => ts.filter(t => t.path !== selected && !t.path.startsWith(selected + '/'))); setActive(null); setSelected(''); setDialog(null); refreshFiles() }} />}
      </Dialog>
      <Dialog open={dialog === 'history'} onClose={() => setDialog(null)} title={`History of ${tab?.path ?? ''}`}>
        {dialog === 'history' && tab && <History id={id} tab={tab} />}
      </Dialog>
      <Dialog open={dialog === 'search'} onClose={() => setDialog(null)} title="Search the files">
        {dialog === 'search' && <Search id={id} onOpen={(path, at) => { setDialog(null); openFile(path, at) }} />}
      </Dialog>
      <Dialog open={dialog === 'version'} onClose={() => setDialog(null)} title="Save a version">
        {dialog === 'version' && <SaveVersion id={id} unsaved={dirty} onDone={() => queryClient.invalidateQueries({ queryKey: ['mission', id] })} />}
      </Dialog>
      <Dialog open={blocker.state === 'blocked'} onClose={() => blocker.reset?.()} title="Leave without saving?">
        <p className="m-0">Some files have unsaved changes. Leaving drops them.</p>
        <div className="mt-4 flex justify-end gap-2.5"><Button onClick={() => blocker.reset?.()}>Stay</Button><Button variant="danger" onClick={() => blocker.proceed?.()}>Leave</Button></div>
      </Dialog>
    </div>
  )
}

function NewFile({ id, folder, onDone }: { id: string; folder: string; onDone: (path: string) => void }) {
  const [path, setPath] = useState(folder ? `${folder}/` : '')
  const create = useMutation({
    mutationFn: () => api<FileContentDto>(`/api/missions/${id}/files/content`, { method: 'PUT', json: { path, content: '', baseSha: null } }),
    onSuccess: c => onDone(c.path),
  })
  return (
    <form className="flex flex-col gap-4" onSubmit={e => { e.preventDefault(); create.mutate() }}>
      <Field label="Path" hint="A new folder is made by putting the file in it, e.g. scripts/intro.sqf" value={path} autoFocus required
        error={create.error ? errorMessage(create.error) : undefined} onChange={e => setPath(e.target.value)} />
      <div className="flex justify-end"><Button type="submit" variant="primary" busy={create.isPending}>Create</Button></div>
    </form>
  )
}

function Rename({ id, from, blocked, onDone }: { id: string; from: string; blocked: boolean; onDone: () => void }) {
  const [to, setTo] = useState(from)
  const rename = useMutation({ mutationFn: () => api(`/api/missions/${id}/files/rename`, { json: { from, to } }), onSuccess: onDone })
  if (blocked) return <p className="m-0">Save or close the open files in <span className="font-mono">{from}</span> first.</p>
  return (
    <form className="flex flex-col gap-4" onSubmit={e => { e.preventDefault(); rename.mutate() }}>
      <Field label="New path" value={to} autoFocus required error={rename.error ? errorMessage(rename.error) : undefined} onChange={e => setTo(e.target.value)} />
      <div className="flex justify-end"><Button type="submit" variant="primary" busy={rename.isPending}>Rename</Button></div>
    </form>
  )
}

function Delete({ id, path, onDone }: { id: string; path: string; onDone: () => void }) {
  const del = useMutation({ mutationFn: () => api(`/api/missions/${id}/files?path=${encodeURIComponent(path)}`, { method: 'DELETE' }), onSuccess: onDone })
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0">Delete <span className="break-all font-mono">{path}</span> from the working copy? Its history keeps every earlier version, and saved mission versions don&apos;t change.</p>
      {del.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(del.error)}</p>}
      <div className="flex justify-end"><Button variant="danger" busy={del.isPending} onClick={() => del.mutate()}>Delete</Button></div>
    </div>
  )
}

function History({ id, tab }: { id: string; tab: Tab }) {
  const revisions = useQuery({ queryKey: ['mission', id, 'history', tab.path], queryFn: () => api<RevisionDto[]>(`/api/missions/${id}/files/history?path=${encodeURIComponent(tab.path)}`) })
  const [pick, setPick] = useState<string | null>(null)
  const old = useQuery({ queryKey: ['mission', id, 'revision', pick], enabled: pick !== null, queryFn: () => api<FileContentDto>(`/api/missions/${id}/files/revision/${pick}`) })
  return (
    <div className="flex flex-col gap-3">
      {revisions.isPending && <Loading />}
      <ul className="m-0 flex max-h-48 list-none flex-col overflow-y-auto p-0 text-sm">
        {revisions.data?.map(r => (
          <li key={r.id}>
            <button type="button" disabled={!r.sha} onClick={() => setPick(r.id)} className={`w-full px-2 py-1.5 text-left ${pick === r.id ? 'bg-accent/15' : 'hover:bg-white/[.04]'} disabled:text-text-muted`}>
              {formatWhen(r.at)} · {r.by ?? '—'} · <span className="text-text-secondary">{r.sha ? r.note ?? 'Saved' : 'Deleted'}</span>
            </button>
          </li>
        ))}
      </ul>
      {old.data && (old.data.text === null
        ? <p className="m-0 text-sm text-text-muted">That revision isn&apos;t text.</p>
        : <><p className="m-0 text-xs text-text-muted">Left: that revision. Right: your current text.</p><CodeDiff path={tab.path} original={old.data.text} modified={tab.draft} height={380} /></>)}
    </div>
  )
}

function Search({ id, onOpen }: { id: string; onOpen: (path: string, line: number) => void }) {
  const [q, setQ] = useState('')
  const [regex, setRegex] = useState(false)
  const search = useMutation({ mutationFn: () => api<SearchHit[]>(`/api/missions/${id}/files/search?q=${encodeURIComponent(q)}&regex=${regex}`) })
  return (
    <div className="flex flex-col gap-3">
      <form className="flex flex-wrap items-end gap-3" onSubmit={e => { e.preventDefault(); search.mutate() }}>
        <Field label="Find" value={q} autoFocus required maxLength={200} className="min-w-56 flex-1" error={search.error ? errorMessage(search.error) : undefined} onChange={e => setQ(e.target.value)} />
        <Check label="Regular expression" checked={regex} onChange={e => setRegex(e.target.checked)} />
        <Button type="submit" busy={search.isPending}>Search</Button>
      </form>
      {search.data && (search.data.length === 0
        ? <p className="m-0 text-text-muted">Nothing found.</p>
        : (
          <ul className="m-0 flex max-h-80 list-none flex-col overflow-y-auto p-0 text-sm">
            {search.data.map((h, i) => (
              <li key={i}>
                <button type="button" className="w-full px-2 py-1 text-left hover:bg-white/[.04]" onClick={() => onOpen(h.path, h.line)}>
                  <span className="font-mono text-xs text-text-secondary">{h.path}:{h.line}</span>
                  <span className="block truncate font-mono text-xs">{h.text}</span>
                </button>
              </li>
            ))}
          </ul>
        ))}
    </div>
  )
}

function SaveVersion({ id, unsaved, onDone }: { id: string; unsaved: boolean; onDone: () => void }) {
  const [note, setNote] = useState('')
  const save = useMutation({ mutationFn: () => api<{ version: number }>(`/api/missions/${id}/versions`, { json: { note } }), onSuccess: onDone })
  if (save.data)
    return <p role="status" className="m-0">Saved version {save.data.version}. It&apos;s going onto the servers; <Link to={`/missions/${id}`} className="text-accent-text">the mission page</Link> shows each one.</p>
  return (
    <form className="flex flex-col gap-4" onSubmit={e => { e.preventDefault(); save.mutate() }}>
      {unsaved && <p className="m-0 text-sm text-accent-text">Some open files have unsaved changes. A version holds only saved files.</p>}
      <p className="m-0 text-sm text-text-secondary">This packs the working copy into the mission&apos;s next version and writes it to every server install.</p>
      <Field label="Note" placeholder="What changed" value={note} maxLength={500} onChange={e => setNote(e.target.value)} />
      {save.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(save.error)}</p>}
      <div className="flex justify-end"><Button type="submit" variant="primary" busy={save.isPending}>Save version</Button></div>
    </form>
  )
}
