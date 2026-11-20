import { CopyIcon, DownloadSimpleIcon, MagnifyingGlassIcon } from '@phosphor-icons/react'
import { useEffect, useRef, useState } from 'react'
import { Button } from '../../components/Button'
import { Panel } from '../../components/Panel'
import { api, errorMessage } from '../../lib/api'
import { useConsole } from '../../lib/hub'
import type { FileEntry, LogLine } from '../../lib/types'

const time = (iso: string) => new Date(iso).toLocaleTimeString('en-GB', { hour12: false })

// The newest RPT, tailed by the agent. Read-only: there is no command input until RCon arrives (Phase 7).
export function ConsolePanel({ serverId, className = '' }: { serverId: string; className?: string }) {
  const { lines, error } = useConsole(serverId, true)
  const [filter, setFilter] = useState('')
  const [follow, setFollow] = useState(true)
  const [note, setNote] = useState('')
  const box = useRef<HTMLDivElement>(null)
  const needle = filter.trim().toLowerCase()
  const visible = needle ? lines.filter(l => l.text.toLowerCase().includes(needle)) : lines

  useEffect(() => {
    if (follow && box.current) box.current.scrollTop = box.current.scrollHeight
  }, [visible.length, follow])

  function onScroll() {
    const el = box.current
    if (el && follow && el.scrollHeight - el.scrollTop - el.clientHeight > 48) setFollow(false)
  }

  async function copy() {
    try {
      await navigator.clipboard.writeText(visible.map(l => `${time(l.readAt)} ${l.text}`).join('\n'))
      setNote(`Copied ${visible.length} line${visible.length === 1 ? '' : 's'}`)
    } catch {
      setNote('Copying needs HTTPS or localhost; select the lines by hand instead.')
    }
  }

  async function download() {
    try {
      const files = await api<FileEntry[]>(`/api/servers/${serverId}/logs`)
      if (files.length === 0) { setNote('There is no log file yet.'); return }
      window.location.href = `/api/servers/${serverId}/logs/${encodeURIComponent(files[0].name)}/download`
    } catch (err) {
      setNote(errorMessage(err))
    }
  }

  return (
    <Panel title="Console" meta="Live server log · read-only" className={className}>
      <div className="flex flex-wrap items-center gap-2 border-b border-line px-4 py-3">
        <label className="flex min-w-48 flex-1 items-center gap-2 border border-line-strong bg-console px-2.5 focus-within:border-accent-text">
          <MagnifyingGlassIcon size={16} aria-hidden="true" className="text-text-muted" />
          <span className="sr-only">Search the log</span>
          <input type="search" value={filter} onChange={e => setFilter(e.target.value)} placeholder="Search the log"
            className="min-h-9 w-full bg-transparent text-sm outline-none pointer-coarse:min-h-11" />
        </label>
        <Button size="sm" onClick={copy}><CopyIcon size={15} aria-hidden="true" />Copy</Button>
        <Button size="sm" aria-pressed={follow} onClick={() => setFollow(f => !f)}
          className="aria-pressed:border-accent-text aria-pressed:text-accent-text">Follow</Button>
        <Button size="sm" onClick={download}><DownloadSimpleIcon size={15} aria-hidden="true" />Download log</Button>
        <span aria-live="polite" className="w-full text-xs text-text-muted empty:hidden">{note}</span>
      </div>
      <div ref={box} onScroll={onScroll} tabIndex={0} aria-label="Server log lines"
        className="h-[26rem] overflow-auto bg-console px-4 py-3 font-mono text-sm leading-relaxed">
        {error && <p role="alert" className="m-0 font-sans text-accent-text">{error}</p>}
        {!error && lines.length === 0 && <p className="m-0 font-sans text-text-muted">No log lines yet. They appear here while the server writes its log.</p>}
        {!error && lines.length > 0 && visible.length === 0 && <p className="m-0 font-sans text-text-muted">No lines match “{filter}”.</p>}
        {visible.map((l: LogLine, i) => (
          <div key={i} className="flex gap-3 whitespace-pre">
            <time dateTime={l.readAt} className="shrink-0 select-none text-text-subtle">{time(l.readAt)}</time>
            <span className="text-text-secondary">{l.text}</span>
          </div>
        ))}
      </div>
      <p className="m-0 border-t border-line px-4 py-2 text-xs text-text-muted">
        {needle ? `${visible.length} of ${lines.length} lines` : `${lines.length} lines`} · Commands need RCon, which arrives in a later phase.
      </p>
    </Panel>
  )
}
