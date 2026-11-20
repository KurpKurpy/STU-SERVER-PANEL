import { DotsThreeIcon, FileIcon, FolderIcon } from '@phosphor-icons/react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { lazy, Suspense, useRef, useState, type FormEvent } from 'react'
import { Button, buttonClasses } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { Field, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { ApiError, api, errorMessage } from '../../lib/api'
import { formatBytes } from '../../lib/format'
import type { FileInstall, FileListing, FileSearchResult, FileSpaceInfo, FileSpacesDto } from '../../lib/types'
import { upload } from '../missions/useMissions'
import { fileQuery, join, nameOf, parentOf } from './paths'

// Monaco is large: it loads only when a file is opened as text.
const TextEditor = lazy(() => import('./TextEditor').then(m => ({ default: m.TextEditor })))

const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-2.5 align-middle'


interface Row { path: string; isFolder: boolean; size: number; modifiedAt: string }
type Action = { kind: 'menu' | 'move' | 'copy' | 'delete' | 'extract'; row: Row } | { kind: 'folder' }

// Phase 4b: the server's own folders, each person only in the folders their permissions name. Deletes and overwrites are backed up
// by the agent first; the program files of the install are never reachable.
export function FilesPage() {
  const spaces = useQuery({ queryKey: ['file-spaces'], queryFn: () => api<FileSpacesDto>('/api/files/spaces') })
  const [installId, setInstallId] = useState<string | null>(null)
  const [spaceName, setSpaceName] = useState<string | null>(null)
  const [path, setPath] = useState('')
  const [query, setQuery] = useState('')
  const [search, setSearch] = useState('')
  const [action, setAction] = useState<Action | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [notice, setNotice] = useState<{ text: string; bad?: boolean } | null>(null)
  const [uploads, setUploads] = useState<{ name: string; progress: number }[]>([])
  const fileInput = useRef<HTMLInputElement>(null)
  const queryClient = useQueryClient()

  const data = spaces.data
  const install = data?.installs.find(i => i.id === installId) ?? data?.installs[0]
  const space = data?.spaces.find(s => s.name === spaceName) ?? data?.spaces[0]
  const canWrite = !!space?.canWrite && !(space.perInstall && install?.readOnly)
  const ready = !!install && !!space
  const at = (p: string) => fileQuery(install!.id, space!.name, p)

  const listing = useQuery({
    queryKey: ['files', install?.id, space?.name, path],
    queryFn: () => api<FileListing>(`/api/files/list?${at(path)}`),
    enabled: ready && !search,
  })
  const found = useQuery({
    queryKey: ['files', 'search', install?.id, space?.name, path, search],
    queryFn: () => api<FileSearchResult>(`/api/files/search?${at(path)}&q=${encodeURIComponent(search)}`),
    enabled: ready && !!search,
  })

  if (spaces.isPending) return <Loading />
  if (spaces.error) return <ErrorNote error={spaces.error} onRetry={() => spaces.refetch()} />
  if (!install || !space) return <Panel title="Files"><p className="m-0 px-6 py-5 text-text-muted">There's no server install yet, or you can't see any of its folders.</p></Panel>

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['files'] })
  const go = (p: string) => { setPath(p); setSearch(''); setQuery(''); setNotice(null) }
  const done = (r: { backup?: string | null; message?: string } | undefined, what: string) => {
    setNotice({ text: r?.message ?? (r?.backup ? `${what} The old copy is in backups\\${r.backup}.` : what) })
    setAction(null)
    refresh()
  }

  async function sendFiles(list: FileList) {
    const files = Array.from(list)
    setUploads(files.map(f => ({ name: f.name, progress: 0 })))
    const url = (overwrite: boolean) => `/api/files/upload?${at(path)}&overwrite=${overwrite}`
    const progress = (name: string) => (p: number) => setUploads(u => u.map(x => (x.name === name ? { ...x, progress: p } : x)))
    const failed: string[] = []
    for (const file of files) {
      const send = (overwrite: boolean) => upload(url(overwrite), [{ file, path: file.name }], progress(file.name), undefined, 'Uploads are at most 2 GB per file.')
      try { await send(false) }
      catch (e) {
        if (e instanceof ApiError && e.code === 'file_failed' && e.message.includes('already there')
          && window.confirm(`Replace ${file.name}? The old one is backed up first.`)) {
          try { await send(true) } catch (again) { failed.push(`${file.name}: ${errorMessage(again)}`) }
        } else failed.push(`${file.name}: ${errorMessage(e)}`)
      }
      setUploads(u => u.filter(x => x.name !== file.name))
    }
    setNotice(failed.length ? { text: failed.join(' '), bad: true } : { text: files.length === 1 ? `Uploaded ${files[0].name}.` : `Uploaded ${files.length} files.` })
    refresh()
  }

  if (editing)
    return (
      <Suspense fallback={<Loading label="Loading the editor…" />}>
        <TextEditor install={install} space={space} path={editing} readOnly={!canWrite} onClose={() => { setEditing(null); refresh() }} />
      </Suspense>
    )

  const rows: Row[] = search
    ? (found.data?.hits ?? []).map(h => ({ path: h.path, isFolder: h.isFolder, size: h.size, modifiedAt: h.modifiedAt }))
    : (listing.data?.items ?? []).map(i => ({ path: join(path, i.name), isFolder: i.isFolder, size: i.size, modifiedAt: i.modifiedAt }))
  const current = search ? found : listing
  const crumbs = path ? path.split('\\') : []

  return (
    <>
      <Panel title="Files" meta={data!.installs.length > 1 && (
        <SelectField label="Server" value={install.id} onChange={e => { setInstallId(e.target.value); go('') }} className="min-w-48">
          {data!.installs.map((i: FileInstall) => <option key={i.id} value={i.id}>{i.name}{i.readOnly ? ' (read-only)' : ''}</option>)}
        </SelectField>
      )}>
        <div className="flex flex-wrap items-end gap-x-6 gap-y-3 px-6 py-4">
          <SelectField label="Folder" value={space.name} onChange={e => { setSpaceName(e.target.value); go('') }} className="min-w-56">
            {data!.spaces.map((s: FileSpaceInfo) => <option key={s.name} value={s.name}>{s.label}</option>)}
          </SelectField>
          <form className="flex items-end gap-2" onSubmit={(e: FormEvent) => { e.preventDefault(); setSearch(query.trim()) }}>
            <Field label="Search names here and below" value={query} onChange={e => setQuery(e.target.value)} className="min-w-48" />
            <Button type="submit">Search</Button>
            {search && <Button onClick={() => go(path)}>Clear</Button>}
          </form>
          {canWrite && (
            <div className="flex flex-wrap gap-2">
              <Button onClick={() => fileInput.current?.click()}>Upload</Button>
              <Button onClick={() => setAction({ kind: 'folder' })}>New folder</Button>
              <input ref={fileInput} type="file" multiple hidden onChange={e => { if (e.target.files?.length) sendFiles(e.target.files); e.target.value = '' }} />
            </div>
          )}
        </div>
        {space.hasPasswords && (
          <p className="m-0 mx-6 mb-4 border border-warn/60 px-4 py-3 text-sm">This folder holds server passwords. Download and share its files with care.</p>
        )}
        {space.perInstall && install.readOnly && <p className="m-0 px-6 pb-4 text-sm text-text-muted">This server folder is read-only on its agent.</p>}

        <nav aria-label="Folder path" className="flex flex-wrap items-center gap-1 border-t border-line px-6 py-3 text-sm">
          <button type="button" className="font-semibold text-accent-text hover:underline" onClick={() => go('')}>{space.label}</button>
          {crumbs.map((c, i) => (
            <span key={i} className="flex items-center gap-1">
              <span aria-hidden className="text-text-subtle">\</span>
              <button type="button" className="break-all text-accent-text hover:underline" onClick={() => go(crumbs.slice(0, i + 1).join('\\'))}>{c}</button>
            </span>
          ))}
          {search && <span className="text-text-muted">· names containing “{search}”</span>}
        </nav>

        {uploads.map(u => (
          <div key={u.name} className="flex items-center gap-3 border-t border-line px-6 py-2 text-sm">
            <span className="min-w-0 flex-1 truncate font-mono">{u.name}</span>
            <div className="h-1.5 w-32 bg-line" aria-hidden><div className="h-full bg-accent" style={{ width: `${Math.round(u.progress * 100)}%` }} /></div>
            <span className="w-16 text-right tabular-nums text-text-secondary">{u.progress < 1 ? `${Math.round(u.progress * 100)}%` : 'Writing…'}</span>
          </div>
        ))}
        {notice && <p role={notice.bad ? 'alert' : 'status'} className={`m-0 border-t border-line px-6 py-3 text-sm ${notice.bad ? 'text-accent-text' : 'text-text-secondary'}`}>{notice.text}</p>}

        {current.isPending && <div className="px-6 py-5"><Loading /></div>}
        {current.error && <div className="px-6 py-5"><ErrorNote error={current.error} onRetry={() => current.refetch()} /></div>}
        {current.data && rows.length === 0 && <p className="m-0 border-t border-line px-6 py-5 text-text-muted">{search ? 'Nothing found.' : 'This folder is empty.'}</p>}
        {rows.length > 0 && (
          <div className="relative overflow-x-auto">
            <table className="w-full min-w-[520px] border-collapse text-sm">
              <thead><tr><th className={th}>Name</th><th className={`${th} text-right`}>Size</th><th className={th}>Modified</th><th className={th}><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {!search && path && (
                  <tr><td className={td} colSpan={4}><button type="button" className="text-accent-text hover:underline" onClick={() => go(parentOf(path))}>.. (up a folder)</button></td></tr>
                )}
                {rows.map(r => (
                  <tr key={r.path}>
                    <td className={td}>
                      <button type="button" className="flex items-start gap-2 text-left text-text hover:text-accent-text"
                        onClick={() => (r.isFolder ? go(r.path) : search ? go(parentOf(r.path)) : setAction({ kind: 'menu', row: r }))}>
                        {r.isFolder ? <FolderIcon size={18} className="mt-0.5 shrink-0 text-accent-text" aria-hidden /> : <FileIcon size={18} className="mt-0.5 shrink-0 text-text-muted" aria-hidden />}
                        <span className="break-all font-mono">{search ? r.path : nameOf(r.path)}</span>
                      </button>
                    </td>
                    <td className={`${td} text-right tabular-nums text-text-secondary`}>{r.isFolder ? '' : formatBytes(r.size)}</td>
                    <td className={`${td} whitespace-nowrap text-text-secondary`}>{formatWhen(r.modifiedAt)}</td>
                    <td className={`${td} text-right`}>
                      <Button size="sm" aria-label={`Actions for ${nameOf(r.path)}`} onClick={() => setAction({ kind: 'menu', row: r })}><DotsThreeIcon size={18} weight="bold" aria-hidden /></Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {current.data?.truncated && <p className="m-0 border-t border-line px-6 py-3 text-xs text-text-muted">{search ? 'Showing the first 500 matches; search for something narrower.' : 'Showing the first 5,000 items.'}</p>}
      </Panel>

      <Dialog open={!!action} onClose={() => setAction(null)} title={action ? titleOf(action) : ''}>
        {action && (
          <ActionBody action={action} canWrite={canWrite} download={(p: string) => `/api/files/download?${at(p)}`}
            onEdit={(p: string) => { setAction(null); setEditing(p) }} onPick={setAction}
            run={async (endpoint, body, what) => {
              const r = endpoint === 'delete'
                ? await api<{ backup?: string; message?: string }>(`/api/files?${at(body.path)}`, { method: 'DELETE' })
                : await api<{ backup?: string; message?: string }>(`/api/files/${endpoint}`, { json: { install: install.id, space: space.name, ...body } })
              done(r, what)
            }}
            folder={path} />
        )}
      </Dialog>
    </>
  )
}

function titleOf(a: Action) {
  if (a.kind === 'folder') return 'New folder'
  const n = nameOf(a.row.path)
  return { menu: n, move: `Rename or move ${n}`, copy: `Copy ${n}`, delete: `Delete ${n}?`, extract: `Extract ${n}` }[a.kind]
}

type Run = (endpoint: 'folder' | 'move' | 'copy' | 'extract' | 'delete', body: { path: string; target?: string }, what: string) => Promise<void>

function ActionBody({ action, canWrite, download, onEdit, onPick, run, folder }: {
  action: Action; canWrite: boolean; download: (p: string) => string; onEdit: (p: string) => void; onPick: (a: Action) => void; run: Run; folder: string
}) {
  if (action.kind === 'folder')
    return <PathForm label="Folder name" initial="" submit="Make folder" onSubmit={name => run('folder', { path: join(folder, name) }, `Made ${name}.`)} />
  const r = action.row
  switch (action.kind) {
    case 'menu':
      return (
        <div className="flex flex-col gap-2">
          {!r.isFolder && <a className={buttonClasses()} href={download(r.path)} download>Download</a>}
          {!r.isFolder && <Button onClick={() => onEdit(r.path)}>{canWrite ? 'Open as text to edit' : 'Open as text'}</Button>}
          {canWrite && <Button onClick={() => onPick({ kind: 'move', row: r })}>Rename or move</Button>}
          {canWrite && <Button onClick={() => onPick({ kind: 'copy', row: r })}>Copy</Button>}
          {canWrite && !r.isFolder && /\.zip$/i.test(r.path) && <Button onClick={() => onPick({ kind: 'extract', row: r })}>Extract here</Button>}
          {canWrite && <Button variant="danger" onClick={() => onPick({ kind: 'delete', row: r })}>Delete</Button>}
          {!canWrite && r.isFolder && <p className="m-0 text-sm text-text-muted">Folders download one file at a time.</p>}
        </div>
      )
    case 'move':
      return <PathForm label="New name or path" initial={r.path} submit="Move" onSubmit={t => run('move', { path: r.path, target: t }, `Moved to ${t}.`)} />
    case 'copy':
      return <PathForm label="Copy to" initial={join(parentOf(r.path), `copy of ${nameOf(r.path)}`)} submit="Copy" onSubmit={t => run('copy', { path: r.path, target: t }, `Copied to ${t}.`)} />
    case 'extract':
      return <PathForm label="Into the new folder" initial={r.path.replace(/\.zip$/i, '')} submit="Extract" onSubmit={t => run('extract', { path: r.path, target: t }, `Extracted into ${t}.`)} />
    case 'delete':
      return <Confirm text={`${r.isFolder ? 'The folder and everything in it' : 'The file'} is moved to the backups folder first, so it can be recovered.`}
        submit="Delete" onConfirm={() => run('delete', { path: r.path }, `Deleted ${nameOf(r.path)}.`)} />
  }
}

function PathForm({ label, initial, submit, onSubmit }: { label: string; initial: string; submit: string; onSubmit: (value: string) => Promise<void> }) {
  const [value, setValue] = useState(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  return (
    <form className="flex flex-col gap-4" onSubmit={async e => {
      e.preventDefault()
      setBusy(true)
      setError(null)
      try { await onSubmit(value.trim()) } catch (x) { setError(errorMessage(x)) } finally { setBusy(false) }
    }}>
      <Field label={label} value={value} onChange={e => setValue(e.target.value)} autoFocus className="font-mono" />
      {error && <p role="alert" className="m-0 text-sm text-accent-text">{error}</p>}
      <div className="flex flex-wrap justify-end gap-3"><Button type="submit" variant="primary" busy={busy} disabled={!value.trim()}>{submit}</Button></div>
    </form>
  )
}

function Confirm({ text, submit, onConfirm }: { text: string; submit: string; onConfirm: () => Promise<void> }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-text-secondary">{text}</p>
      {error && <p role="alert" className="m-0 text-sm text-accent-text">{error}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button variant="danger" busy={busy} onClick={async () => {
          setBusy(true)
          setError(null)
          try { await onConfirm() } catch (x) { setError(errorMessage(x)) } finally { setBusy(false) }
        }}>{submit}</Button>
      </div>
    </div>
  )
}
