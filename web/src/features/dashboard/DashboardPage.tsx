import { PlusIcon, PushPinIcon } from '@phosphor-icons/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Button, buttonClasses } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { Check, Field, TextArea } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan, useMe } from '../../lib/auth'
import { formatBytes, formatDuration, isRunning, missionAndMap, stateLabel, stateTone } from '../../lib/format'
import type { ActivityDto, AnnouncementDto, NextScheduled, ServerDto } from '../../lib/types'
import { taskLabels } from '../schedules/SchedulesPage'
import { ServerControls } from '../server/ServerControls'
import { OperationList } from '../operations/OperationsPage'
import type { OperationRow } from '../../lib/types'
import { useNow } from '../server/ServerPage'

export function DashboardPage() {
  const can = useCan()
  const servers = useQuery({ queryKey: ['servers'], queryFn: () => api<ServerDto[]>('/api/servers'), enabled: can('server.view'), refetchInterval: 5000 })
  // A failed refresh keeps the last data; it must not keep claiming the server's state.
  const live = !servers.isError

  return (
    <>
      <Hero server={can('server.view') ? servers.data?.[0] : undefined} live={live} />
      <div className="stagger grid grid-cols-1 gap-6 md:grid-cols-12">
        {can('server.view') && (
          <Panel title="Server" className="md:col-span-7">
            {servers.isPending && <div className="px-6 py-5"><Loading /></div>}
            {servers.error && <div className="px-6 py-5"><ErrorNote error={servers.error} onRetry={() => servers.refetch()} /></div>}
            {servers.data?.length === 0 && (
              <div className="flex flex-col items-start gap-3 px-6 py-5">
                <p className="m-0 max-w-prose text-text-secondary">No server set up yet.</p>
                {(can('agents.manage') || can('profiles.manage')) && <Link to="/server/setup" className={buttonClasses('default', 'sm')}>Set up the server</Link>}
              </div>
            )}
            {servers.data?.map(s => <ServerSummary key={s.id} server={s} live={live} />)}
          </Panel>
        )}
        <AnnouncementsPanel className={can('server.view') ? 'md:col-span-5' : 'md:col-span-12'} />
        {can('operations.view') && <UpcomingOperationsPanel />}
        {can('server.view') && <NextScheduledPanel manage={can('schedules.manage')} />}
        <ActivityPanel />
      </div>
    </>
  )
}

// One server on the dashboard: everything the REST view knows, refreshed every 5 s, plus its controls.
function ServerSummary({ server: s, live }: { server: ServerDto; live: boolean }) {
  const now = useNow()
  const known = live && s.state !== 'unknown'
  const facts: [string, string][] = [
    ['Address', s.address ? `${s.address}:${s.port}` : `Port ${s.port}`],
    ['Mission and map', known && s.query ? missionAndMap(s.query) || '—' : '—'],
    ['Players', known && s.query ? `${s.query.players} / ${s.query.maxPlayers}` : '—'],
    ['Uptime', known && isRunning(s.state) && s.startedAt ? formatDuration(now - Date.parse(s.startedAt)) : '—'],
    ['Last started', formatWhen(s.lastStartedAt)],
    ['CPU / memory', known && s.process ? `${s.process.cpuPercent.toFixed(1)}% · ${formatBytes(s.process.memoryBytes)}` : '—'],
    ['Disk free', known && s.disk ? `${formatBytes(s.disk.freeBytes)} of ${formatBytes(s.disk.totalBytes)}` : '—'],
  ]
  return (
    <div className="flex flex-col gap-4 border-b border-line px-6 py-5 last:border-b-0">
      <div className="flex flex-wrap items-center gap-3">
        {live ? <StatusBlock tone={stateTone[s.state]}>{stateLabel[s.state]}</StatusBlock> : <StatusBlock tone="off">Not live</StatusBlock>}
        <h3 className="m-0 text-lg font-semibold">{s.name}</h3>
        <Link to={`/server?p=${s.id}`} className={`${buttonClasses('default', 'sm')} ml-auto`}>Open server</Link>
      </div>
      {!live && <p className="m-0 text-sm text-text-muted">Can't reach the panel right now, so the server's state isn't shown.</p>}
      {live && !known && <p className="m-0 text-sm text-text-muted">The server agent isn't connected, so the server's state is unknown.</p>}
      {s.state === 'error' && s.error && <p className="m-0 text-sm text-accent-text">{s.error}</p>}
      <dl className="m-0 grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2">
        {facts.map(([k, v]) => (
          <div key={k} className="flex min-w-0 flex-col">
            <dt className="text-2xs font-semibold uppercase tracking-label text-text-muted">{k}</dt>
            <dd className="m-0 truncate font-mono text-sm">{v}</dd>
          </div>
        ))}
      </dl>
      {known && <ServerControls server={s} />}
    </div>
  )
}

// The one big moment on the dashboard: an in-game photo under a scrim, the unit name, who is signed in and,
// for members who can see it, the first server's state.
function Hero({ server, live }: { server: ServerDto | undefined; live: boolean }) {
  const { data: me } = useMe()
  const role = me ? (me.isOwner ? 'Owner' : me.roles[0] ?? 'Member') : ''
  return (
    <section aria-label="Unit" className="chamfer relative isolate flex min-h-56 flex-col justify-end gap-3 overflow-hidden px-6 py-6 md:min-h-64 md:px-10 md:py-8">
      <img src="/brand/pelican-deploy-1920.webp" srcSet="/brand/pelican-deploy-960.webp 960w, /brand/pelican-deploy-1920.webp 1920w"
        sizes="(min-width: 56.25rem) 75vw, 100vw" alt="" className="absolute inset-0 -z-10 size-full object-cover object-[70%_55%]" />
      <div aria-hidden="true" className="absolute inset-0 -z-10 bg-linear-to-r from-bg from-25% via-bg/75 via-55% to-transparent max-md:via-bg/85 max-md:via-70%" />
      <div aria-hidden="true" className="absolute inset-0 -z-10 bg-linear-to-t from-bg/60 to-transparent" />
      {me && <p className="m-0 text-xs font-semibold uppercase tracking-label text-accent-text">Signed in as {me.userName} · {role}</p>}
      <p className="display m-0 text-[46px] md:text-[72px]">13th Special<br />Tactics Unit</p>
      {server && !live && (
        <p className="m-0 flex flex-wrap items-center gap-2.5 text-text-secondary">
          <StatusBlock tone="off">Not live</StatusBlock>Can't reach the panel
        </p>
      )}
      {server && live && (
        <p className="m-0 flex flex-wrap items-center gap-2.5 text-text-secondary">
          <StatusBlock tone={stateTone[server.state]}>{stateLabel[server.state]}</StatusBlock>
          {server.state === 'unknown'
            ? 'Agent not connected'
            : <span>{server.name}{server.query && <span className="font-mono"> · {[server.query.map, `${server.query.players}/${server.query.maxPlayers}`].filter(Boolean).join(' · ')}</span>}</span>}
        </p>
      )}
    </section>
  )
}
function AnnouncementsPanel({ className }: { className: string }) {
  const can = useCan()
  const manage = can('announcements.manage')
  const list = useQuery({ queryKey: ['announcements'], queryFn: () => api<AnnouncementDto[]>('/api/announcements') })
  const [editing, setEditing] = useState<AnnouncementDto | 'new' | null>(null)
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/announcements/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['announcements'] }),
  })

  return (
    <Panel title="Announcements" className={className}
      meta={manage && <Button size="sm" onClick={() => setEditing('new')}><PlusIcon size={14} weight="bold" aria-hidden="true" />New</Button>}>
      <div className="stagger flex flex-col">
        {list.isPending && <div className="px-6 py-5"><Loading /></div>}
        {list.error && <div className="px-6 py-5"><ErrorNote error={list.error} onRetry={() => list.refetch()} /></div>}
        {list.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No announcements yet.</p>}
        {list.data?.map(a => (
          <article key={a.id} className="flex flex-col gap-1.5 border-b border-line px-6 py-4 last:border-b-0">
            <span className={`flex items-center gap-1.5 text-xs font-semibold uppercase tracking-label ${a.pinned ? 'text-accent-text' : 'text-text-muted'}`}>
              {a.pinned && <><PushPinIcon size={13} weight="fill" aria-hidden="true" />Pinned ·</>} {a.author} · <time dateTime={a.createdAt}>{formatWhen(a.createdAt)}</time>
            </span>
            <h3 className="m-0 text-lg font-semibold">{a.title}</h3>
            <p className="m-0 max-w-prose whitespace-pre-line leading-relaxed text-text-secondary">{a.body}</p>
            {manage && (
              <div className="flex gap-2 pt-1">
                <Button size="sm" onClick={() => setEditing(a)}>Edit</Button>
                <Button size="sm" variant="danger" busy={remove.isPending && remove.variables === a.id}
                  onClick={() => { if (confirm(`Delete “${a.title}”?`)) remove.mutate(a.id) }}>Delete</Button>
              </div>
            )}
          </article>
        ))}
      </div>
      <Dialog open={editing !== null} onClose={() => setEditing(null)} title={editing === 'new' ? 'New announcement' : 'Edit announcement'}>
        {editing !== null && <AnnouncementForm current={editing === 'new' ? null : editing} onDone={() => setEditing(null)} />}
      </Dialog>
    </Panel>
  )
}

function AnnouncementForm({ current, onDone }: { current: AnnouncementDto | null; onDone: () => void }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (body: { title: string; body: string; pinned: boolean }) =>
      current ? api(`/api/announcements/${current.id}`, { method: 'PUT', json: body }) : api('/api/announcements', { json: body }),
    onSuccess: async () => {
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['announcements'] }), queryClient.invalidateQueries({ queryKey: ['activity'] })])
      onDone()
    },
    onError: err => setError(errorMessage(err)),
  })

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    save.mutate({ title: String(form.get('title') ?? ''), body: String(form.get('body') ?? ''), pinned: form.get('pinned') === 'on' })
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <Field label="Title" name="title" defaultValue={current?.title} maxLength={120} required autoFocus />
      <TextArea label="Text" name="body" defaultValue={current?.body} maxLength={4000} required rows={6} />
      <Check label="Pin to the top" name="pinned" defaultChecked={current?.pinned} />
      {error && <p role="alert" className="m-0 text-accent-text">{error}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant="primary" busy={save.isPending}>{current ? 'Save' : 'Post'}</Button>
      </div>
    </form>
  )
}

function ActivityPanel() {
  const activity = useQuery({
    queryKey: ['activity'],
    queryFn: () => api<ActivityDto[]>('/api/activity?limit=20'),
    refetchInterval: 60_000,
    refetchOnWindowFocus: true,
  })

  return (
    <Panel title="Recent activity" className="md:col-span-12">
      {/* Animated list: rows cascade in on first load; on refresh only new rows (fresh keys) animate. */}
      <div className="stagger flex flex-col py-2">
        {activity.isPending && <div className="px-6 py-3"><Loading /></div>}
        {activity.error && <div className="px-6 py-3"><ErrorNote error={activity.error} onRetry={() => activity.refetch()} /></div>}
        {activity.data?.length === 0 && <p className="m-0 px-6 py-3 text-text-muted">Nothing yet.</p>}
        {activity.data?.map(a => (
          <div key={a.id} className="grid grid-cols-[112px_112px_minmax(0,1fr)] items-baseline gap-3.5 px-6 py-2 max-sm:grid-cols-1 max-sm:gap-0.5">
            <time dateTime={a.at} className="font-mono text-sm text-text-muted">{formatWhen(a.at)}</time>
            <span className="font-semibold">{a.actor}</span>
            <span className="min-w-0 text-text-secondary">{a.summary}</span>
          </div>
        ))}
      </div>
    </Panel>
  )
}

// Phase 6a Ruling 14: the next five planned operations, with readiness as last checked.
// Phase 7b: the next three scheduled runs (restarts, updates, backups).
function NextScheduledPanel({ manage }: { manage: boolean }) {
  const next = useQuery({ queryKey: ['schedules', 'next'], queryFn: () => api<NextScheduled[]>('/api/schedules/next'), refetchInterval: 60_000 })
  if (next.data?.length === 0 && !manage) return null
  return (
    <Panel title="Next scheduled" className="md:col-span-12" meta={manage ? <Link to="/schedules" className="text-sm">All schedules</Link> : undefined}>
      {next.isPending && <div className="px-6 py-5"><Loading /></div>}
      {next.error && <div className="px-6 py-5"><ErrorNote error={next.error} onRetry={() => next.refetch()} /></div>}
      {next.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">Nothing scheduled.</p>}
      {next.data && next.data.length > 0 && (
        <ul className="m-0 list-none p-0">
          {next.data.map(n => (
            <li key={n.id} className="flex flex-wrap items-baseline gap-x-4 border-t border-line px-6 py-3 text-sm first:border-t-0">
              <span className="min-w-28 font-mono tabular-nums">{formatWhen(n.at)}</span>
              <span className="font-semibold">{n.name}</span>
              <span className="text-text-secondary">{taskLabels[n.task]}</span>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  )
}

function UpcomingOperationsPanel() {
  const ops = useQuery({ queryKey: ['operations', 'upcoming', ''], queryFn: () => api<OperationRow[]>('/api/operations') })
  return (
    <Panel title="Upcoming operations" className="md:col-span-12" meta={<Link to="/operations" className="text-sm">All operations</Link>}>
      {ops.isPending && <div className="px-6 py-5"><Loading /></div>}
      {ops.error && <div className="px-6 py-5"><ErrorNote error={ops.error} onRetry={() => ops.refetch()} /></div>}
      {ops.data && <OperationList rows={ops.data.slice(0, 5)} empty="Nothing planned yet." />}
    </Panel>
  )
}
