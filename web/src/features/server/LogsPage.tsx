import { DownloadSimpleIcon, MagnifyingGlassIcon } from '@phosphor-icons/react'
import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { buttonClasses } from '../../components/Button'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { api } from '../../lib/api'
import { formatBytes } from '../../lib/format'
import type { FileEntry } from '../../lib/types'
import { NoServerYet } from './ServerPage'
import { ServerPicker, useServers } from './ServerLayout'

const TAIL = 262144
const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-2.5'

async function fetchText(path: string): Promise<string> {
  const res = await fetch(path, { credentials: 'same-origin' })
  if (res.ok) return res.text()
  let message = 'Something went wrong. Try again.'
  try { message = ((await res.json()) as { message?: string }).message ?? message } catch { /* not JSON */ }
  throw new Error(message)
}

// Historic server logs: the RPT files in the profile's folder on the box, read through the agent.
export function LogsPage() {
  const { servers, selected, select } = useServers()
  const [file, setFile] = useState<string | null>(null)
  const files = useQuery({
    queryKey: ['logs', selected?.id],
    queryFn: () => api<FileEntry[]>(`/api/servers/${selected!.id}/logs`),
    enabled: !!selected,
    retry: false,
  })

  if (servers.isPending) return <Loading />
  if (servers.error && !servers.data) return <ErrorNote error={servers.error} onRetry={() => servers.refetch()} />
  if (!selected) return <NoServerYet />
  const chosen = files.data?.find(f => f.name === file) ?? null

  return (
    <div className="grid grid-cols-1 items-start gap-6 lg:grid-cols-12">
      <Panel title="Log files" className="lg:col-span-5"
        meta={<ServerPicker servers={servers.data} selected={selected} onSelect={id => { setFile(null); select(id) }} />}>
        {files.isPending && <div className="px-6 py-5"><Loading /></div>}
        {files.error && <div className="px-6 py-5"><ErrorNote error={files.error} onRetry={() => files.refetch()} /></div>}
        {files.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No log files yet. The server writes one each time it starts.</p>}
        {files.data && files.data.length > 0 && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse">
              <thead><tr><th className={th}>File</th><th className={th}>Size</th><th className={th}>Modified</th></tr></thead>
              <tbody>
                {files.data.map(f => (
                  <tr key={f.name} className={f.name === file ? 'bg-white/6' : 'hover:bg-white/[.03]'}>
                    <td className={td}>
                      <button type="button" onClick={() => setFile(f.name)} aria-current={f.name === file ? 'true' : undefined}
                        className="min-h-9 break-all text-left font-mono text-sm text-text hover:text-accent-text pointer-coarse:min-h-11">{f.name}</button>
                    </td>
                    <td className={`${td} whitespace-nowrap font-mono text-sm text-text-muted`}>{formatBytes(f.size)}</td>
                    <td className={`${td} whitespace-nowrap font-mono text-sm text-text-muted`}>{formatWhen(f.modifiedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <div className="lg:col-span-7">
        {chosen
          ? <LogViewer key={chosen.name} serverId={selected.id} file={chosen} />
          : <Panel title="Log"><p className="m-0 px-6 py-5 text-text-muted">Pick a log file to read it.</p></Panel>}
      </div>
    </div>
  )
}

function LogViewer({ serverId, file }: { serverId: string; file: FileEntry }) {
  const [filter, setFilter] = useState('')
  const base = `/api/servers/${serverId}/logs/${encodeURIComponent(file.name)}`
  const text = useQuery({ queryKey: ['log', serverId, file.name], queryFn: () => fetchText(`${base}?tailBytes=${TAIL}`), retry: false })
  const lines = text.data ? text.data.split(/\r?\n/).filter((l, i, all) => l !== '' || i < all.length - 1) : []
  const needle = filter.trim().toLowerCase()
  const visible = needle ? lines.filter(l => l.toLowerCase().includes(needle)) : lines

  return (
    <Panel title="Log" meta={<a href={`${base}/download`} className={buttonClasses('default', 'sm')}><DownloadSimpleIcon size={15} aria-hidden="true" />Download</a>}>
      <div className="flex flex-wrap items-center gap-3 border-b border-line px-4 py-3">
        <label className="flex min-w-48 flex-1 items-center gap-2 border border-line-strong bg-console px-2.5 focus-within:border-accent-text">
          <MagnifyingGlassIcon size={16} aria-hidden="true" className="text-text-muted" />
          <span className="sr-only">Search this log</span>
          <input type="search" value={filter} onChange={e => setFilter(e.target.value)} placeholder="Search this log"
            className="min-h-9 w-full bg-transparent text-sm outline-none pointer-coarse:min-h-11" />
        </label>
        <span className="text-xs text-text-muted">
          {needle ? `${visible.length} of ${lines.length} lines` : `${lines.length} lines`} · {file.size > TAIL ? 'Showing the last 256 KB' : 'Showing the whole file'}
        </span>
      </div>
      <div tabIndex={0} aria-label={`Lines of ${file.name}`} className="h-[32rem] overflow-auto bg-console px-4 py-3 font-mono text-sm leading-relaxed">
        {text.isPending && <Loading />}
        {text.error && <ErrorNote error={text.error} onRetry={() => text.refetch()} />}
        {visible.map((l, i) => <div key={i} className="whitespace-pre text-text-secondary">{l || ' '}</div>)}
      </div>
    </Panel>
  )
}
