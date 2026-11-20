import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { Button } from '../../components/Button'
import { api, errorMessage } from '../../lib/api'
import { CopyField } from '../../components/CopyField'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { Sparkline } from '../../components/Sparkline'
import { StatCard } from '../../components/StatCard'
import { StatusBlock } from '../../components/StatusBlock'
import { buttonClasses } from '../../components/Button'
import { useCan } from '../../lib/auth'
import { formatBytes, formatDuration, formatRate, isRunning, missionAndMap, stateLabel, stateTone } from '../../lib/format'
import { useServerLive } from '../../lib/hub'
import type { JoinInfo, ServerDto } from '../../lib/types'
import { ConsolePanel } from './ConsolePanel'
import { ServerControls } from './ServerControls'
import { ServerPicker, useServers } from './ServerLayout'

export function useNow(intervalMs = 1000) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(t)
  }, [intervalMs])
  return now
}

// Design §5.2's one exception: members who may see it get the applied join password (hidden until asked).
function JoinCard({ serverId }: { serverId: string }) {
  const join = useQuery({ queryKey: ['join', serverId], queryFn: () => api<JoinInfo>(`/api/servers/${serverId}/join`) })
  const [show, setShow] = useState(false)
  if (!join.data) return null
  const password = join.data.password
  return (
    <StatCard label="Join password" value={password === null ? 'None' : show ? <span className="font-mono">{password}</span> : '••••••••'}
      hint={password !== null && (
        <span className="flex flex-col gap-2 pt-1">
          <Button size="sm" className="self-start" onClick={() => setShow(s => !s)}>{show ? 'Hide' : 'Show'}</Button>
          {show && <CopyField value={password} label="join password" />}
        </span>
      )} />
  )
}

// The applied revision's headless clients: running count, plus Start/Stop while the server runs.
function HeadlessRow({ server, running }: { server: ServerDto; running: boolean }) {
  const can = useCan()
  const queryClient = useQueryClient()
  const hc = server.headless!
  const act = useMutation({
    mutationFn: (what: 'start' | 'stop') => api(`/api/servers/${server.id}/headless/${what}`, { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['servers'] }),
  })
  return (
    <div className="flex flex-wrap items-center gap-3 border-t border-line px-6 py-3 text-sm">
      <span className="font-semibold">Headless clients</span>
      <StatusBlock tone={hc.running === hc.count ? 'ok' : hc.running > 0 ? 'warn' : 'off'}>{hc.running} of {hc.count} running</StatusBlock>
      {hc.withServer && <span className="text-text-muted">Start with the server</span>}
      {can('server.control') && running && (
        <span className="flex gap-2">
          {hc.running < hc.count && <Button size="sm" busy={act.isPending} onClick={() => act.mutate('start')}>Start headless clients</Button>}
          {hc.running > 0 && <Button size="sm" busy={act.isPending} onClick={() => act.mutate('stop')}>Stop headless clients</Button>}
        </span>
      )}
      {act.error && <span role="alert" className="text-accent-text">{errorMessage(act.error)}</span>}
    </div>
  )
}

export function NoServerYet() {
  const can = useCan()
  return (
    <Panel title="Server">
      <div className="flex flex-col items-start gap-3 px-6 py-5">
        <p className="m-0 max-w-prose text-text-secondary">No server set up yet.</p>
        {(can('agents.manage') || can('profiles.manage')) && <Link to="/server/setup" className={buttonClasses('default', 'sm')}>Set up the server</Link>}
      </div>
    </Panel>
  )
}

export function ServerPage() {
  const can = useCan()
  const { servers, selected, select } = useServers()
  if (!can('server.view')) return <NoAccess />
  if (servers.isPending) return <Loading />
  if (servers.error && !servers.data) return <ErrorNote error={servers.error} onRetry={() => servers.refetch()} />
  if (!selected) return <NoServerYet />
  return <LiveServer key={selected.id} base={selected} all={servers.data} onSelect={select} />
}

function LiveServer({ base, all, onSelect }: { base: ServerDto; all: ServerDto[]; onSelect: (id: string) => void }) {
  const can = useCan()
  const { server: live, resources, link, error } = useServerLive(base.id)
  const now = useNow()
  const server = live ?? base
  // Nothing stale: without a live link, or with the agent away, nothing below claims to be current.
  const linkDown = link !== 'live'
  const notLive = linkDown || server.state === 'unknown'
  const why = linkDown ? 'Not live: reconnecting to the panel' : server.state === 'unknown' ? 'The server agent is offline' : null
  const running = !notLive && isRunning(server.state)
  const dash = (reason: string) => ({ value: '—', hint: why ?? reason })
  const latest = resources.at(-1)
  const proc = (r: (typeof resources)[number]) => r.processes.find(p => p.profileId === server.id)

  const stats = [
    { label: 'Address', value: server.address ? `${server.address}:${server.port}` : `Port ${server.port}`, hint: server.address ? undefined : 'No public address set' },
    running && server.startedAt
      ? { label: 'Uptime', value: formatDuration(now - Date.parse(server.startedAt)), hint: `Since ${formatWhen(server.startedAt)}` }
      : { label: 'Uptime', ...dash(server.lastStartedAt ? `Last started ${formatWhen(server.lastStartedAt)}` : 'Not running') },
    !notLive && server.query
      ? { label: 'Mission and map', value: missionAndMap(server.query) || '—', hint: server.query.map ? undefined : 'No mission loaded' }
      : { label: 'Mission and map', ...dash(server.state === 'starting' ? 'Waiting for the first answer to a status query' : 'Not running') },
    !notLive && server.query
      ? { label: 'Players', value: `${server.query.players} / ${server.query.maxPlayers}` }
      : { label: 'Players', ...dash('Not answering status queries') },
    !notLive && server.process
      ? { label: 'CPU (server)', value: `${server.process.cpuPercent.toFixed(1)}%`, hint: 'Share of the whole machine' }
      : { label: 'CPU (server)', ...dash('Not running') },
    !notLive && server.process
      ? { label: 'Memory (server)', value: formatBytes(server.process.memoryBytes) }
      : { label: 'Memory (server)', ...dash('Not running') },
    !notLive && server.disk
      ? { label: 'Disk free', value: formatBytes(server.disk.freeBytes), hint: `of ${formatBytes(server.disk.totalBytes)} on the install drive` }
      : { label: 'Disk free', ...dash('The agent hasn’t reported disk space yet') },
    !notLive && latest
      ? { label: 'Network (host)', value: `In ${formatRate(latest.netInBytesPerSec)}`, hint: `Out ${formatRate(latest.netOutBytesPerSec)} · whole machine, not just the server` }
      : { label: 'Network (host)', ...dash('No sample yet') },
  ]

  return (
    <>
      <Panel title={server.name} meta={!notLive && <ServerControls server={server} />}>
        <div className="flex flex-wrap items-center gap-x-5 gap-y-3 px-6 py-4">
          {linkDown ? <StatusBlock tone="off">Not live</StatusBlock> : <StatusBlock tone={stateTone[server.state]}>{stateLabel[server.state]}</StatusBlock>}
          {server.address && <div className="w-72 max-w-full"><CopyField value={`${server.address}:${server.port}`} label="server address" /></div>}
          <ServerPicker servers={all} selected={base} onSelect={onSelect} />
          <span className="text-sm text-text-muted">{server.installName}</span>
        </div>
        {server.headless && !notLive && <HeadlessRow server={server} running={running} />}
        {(why || server.error || error) && (
          <p role={server.state === 'error' || error ? 'alert' : undefined} className="m-0 border-t border-line px-6 py-3 text-sm text-text-secondary">
            {error ?? why ?? server.error}
          </p>
        )}
      </Panel>

      {can('profiles.apply') && server.appliedRevision !== null && server.revision > server.appliedRevision && (
        <Panel title="Unapplied changes" emphasis>
          <p className="m-0 px-6 py-4 text-sm">
            The profile is at revision {server.revision}; the server has revision {server.appliedRevision}.{' '}
            <Link to={`/profiles/${server.id}`}>Open the profile</Link> to apply while the server is stopped.
          </p>
        </Panel>
      )}

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-12">
        {can('logs.view') && <ConsolePanel serverId={server.id} className="lg:col-span-8" />}
        <div className={`grid grid-cols-1 content-start gap-3 sm:grid-cols-2 ${can('logs.view') ? 'lg:col-span-4 lg:grid-cols-1' : 'lg:col-span-12 lg:grid-cols-4'}`}>
          {can('server.viewJoinInfo') && <JoinCard serverId={server.id} />}
          {stats.map(s => <StatCard key={s.label} label={s.label} value={s.value} hint={s.hint} />)}
        </div>
      </div>

      {notLive ? (
        <p className="m-0 text-sm text-text-muted">History charts come back when the live link and the agent are back.</p>
      ) : (
        <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <Sparkline label="CPU" max={100} format={n => `${n.toFixed(0)}%`} series={[
            { name: 'Server', values: resources.map(r => proc(r)?.cpuPercent ?? 0) },
            { name: 'Host', values: resources.map(r => r.hostCpuPercent), dashed: true },
          ]} />
          <Sparkline label="Memory (server)" format={formatBytes} series={[{ name: 'Server', values: resources.map(r => proc(r)?.memoryBytes ?? 0) }]} />
          <Sparkline label="Network (host)" format={formatRate} series={[
            { name: 'In', values: resources.map(r => r.netInBytesPerSec) },
            { name: 'Out', values: resources.map(r => r.netOutBytesPerSec), dashed: true },
          ]} />
        </div>
      )}
    </>
  )
}
