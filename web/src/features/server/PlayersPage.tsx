import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock, type Tone } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { BansDto, PlayerDto, PlayersDto, RconLine } from '../../lib/types'
import { NoServerYet } from './ServerPage'
import { ServerPicker, useServers } from './ServerLayout'

const rconTone: Record<string, Tone> = { Connected: 'ok', Connecting: 'warn', Failed: 'bad', Off: 'off' }
const since = (iso: string | null) => { if (!iso) return '—'; const m = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000)); return m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${m % 60} min` }

// Phase 7a Ruling 12: who's on, through BattlEye RCon; kick, ban, unban and messages for players.manage; the command box for server.console.
export function PlayersPage() {
  const can = useCan()
  const { servers, selected, select } = useServers()
  const manage = can('players.manage')
  const players = useQuery({
    queryKey: ['players', selected?.id], enabled: !!selected, refetchInterval: 10_000,
    queryFn: () => api<PlayersDto>(`/api/servers/${selected!.id}/players`),
  })
  const [acting, setActing] = useState<{ kind: 'kick' | 'ban'; player: PlayerDto } | null>(null)

  if (servers.isPending) return <Loading />
  if (servers.error) return <ErrorNote error={servers.error} onRetry={() => servers.refetch()} />
  if (!selected) return <NoServerYet />
  const d = players.data
  return (
    <div className="stagger grid grid-cols-1 items-start gap-6 md:grid-cols-12">
      <Panel title="Players" className="md:col-span-12" meta={<ServerPicker servers={servers.data!} selected={selected} onSelect={select} />}>
        <div className="flex flex-wrap items-center gap-2 border-b border-line px-6 py-3 text-sm">
          <span className="text-text-muted">RCon</span>
          {d && <StatusBlock tone={rconTone[d.rcon] ?? 'off'}>{d.rcon === 'Off' ? 'Off' : d.rcon}</StatusBlock>}
          {d?.rconError && <span className="text-text-secondary">{d.rconError}</span>}
        </div>
        {players.isPending && <div className="px-6 py-5"><Loading /></div>}
        {players.error && <div className="px-6 py-5"><ErrorNote error={players.error} onRetry={() => players.refetch()} /></div>}
        {d?.unavailable && <p className="m-0 px-6 py-5 text-text-secondary">The player list isn't available: {d.unavailable}</p>}
        {d && !d.unavailable && d.players.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">Nobody's on.</p>}
        {d && d.players.length > 0 && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-sm">
              <thead>
                <tr className="text-left text-2xs font-semibold uppercase tracking-label text-text-muted">
                  <th scope="col" className="px-6 py-2">#</th><th scope="col" className="px-3 py-2">Name</th><th scope="col" className="px-3 py-2">Ping</th>
                  <th scope="col" className="px-3 py-2">On for</th>
                  {manage && <><th scope="col" className="px-3 py-2">GUID</th><th scope="col" className="px-3 py-2">IP</th><th scope="col" className="px-3 py-2"><span className="sr-only">Actions</span></th></>}
                </tr>
              </thead>
              <tbody>
                {d.players.map(pl => (
                  <tr key={`${pl.number}-${pl.guid ?? pl.name}`} className="border-t border-line">
                    <td className="px-6 py-2 font-mono tabular-nums">{pl.number}</td>
                    <td className="px-3 py-2 font-semibold">{pl.name}{pl.lobby && <span className="ml-2 text-xs font-normal text-text-muted">in the lobby</span>}</td>
                    <td className="px-3 py-2 font-mono tabular-nums">{pl.ping} ms</td>
                    <td className="px-3 py-2 font-mono tabular-nums" title="Since this console first saw them">{since(pl.firstSeen)}</td>
                    {manage && (
                      <>
                        <td className="px-3 py-2 font-mono text-xs" title={pl.verified ? 'Verified by BattlEye' : 'Not verified yet'}>{pl.guid?.slice(0, 8)}…{pl.verified ? '' : ' (?)'}</td>
                        <td className="px-3 py-2 font-mono text-xs">{pl.ip}</td>
                        <td className="px-3 py-2">
                          <div className="flex gap-2">
                            <Button size="sm" onClick={() => setActing({ kind: 'kick', player: pl })}>Kick</Button>
                            <Button size="sm" variant="danger" onClick={() => setActing({ kind: 'ban', player: pl })}>Ban</Button>
                          </div>
                        </td>
                      </>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {d?.unparsed && d.unparsed.length > 0 && (
          <details className="border-t border-line px-6 py-3 text-sm"><summary>Lines BattlEye sent that this console couldn't read ({d.unparsed.length})</summary>
            <pre className="m-0 mt-2 overflow-x-auto font-mono text-xs">{d.unparsed.join('\n')}</pre></details>
        )}
        {manage && <SayForm serverId={selected.id} />}
      </Panel>

      {manage && <BansPanel serverId={selected.id} />}
      {manage && <RconLogPanel serverId={selected.id} console={can('server.console')} />}

      <Dialog open={!!acting} onClose={() => setActing(null)} title={acting?.kind === 'ban' ? `Ban ${acting.player.name}` : `Kick ${acting?.player.name ?? ''}`}>
        {acting && <ActionForm serverId={selected.id} serverName={selected.name} kind={acting.kind} player={acting.player} onDone={() => { setActing(null); void players.refetch() }} />}
      </Dialog>
    </div>
  )
}

function ActionForm({ serverId, serverName, kind, player, onDone }: { serverId: string; serverName: string; kind: 'kick' | 'ban'; player: PlayerDto; onDone: () => void }) {
  const [reason, setReason] = useState('')
  const [minutes, setMinutes] = useState('0')
  const act = useMutation({
    mutationFn: () => api(`/api/servers/${serverId}/players/${kind}`, {
      json: kind === 'ban' ? { number: player.number, guid: player.guid, minutes: Number(minutes || '0'), reason } : { number: player.number, guid: player.guid, reason },
    }),
    onSuccess: onDone,
  })
  const length = Number(minutes || '0') === 0 ? 'permanently' : `for ${minutes} minutes`
  return (
    <form className="flex flex-col gap-4" onSubmit={(e: FormEvent) => { e.preventDefault(); act.mutate() }}>
      <p className="m-0 text-text-secondary">
        {kind === 'kick'
          ? <>Kicks <strong className="text-text">{player.name}</strong> (#{player.number}) from {serverName}. They can join again.</>
          : <>Bans <strong className="text-text">{player.name}</strong> (#{player.number}) from {serverName} <strong className="text-text">{length}</strong>, by their BattlEye GUID.</>}
      </p>
      {kind === 'ban' && <Field label="Minutes (0 = permanent)" type="number" min={0} max={525600} value={minutes} onChange={e => setMinutes(e.target.value)} />}
      <Field label="Reason (they see it)" value={reason} maxLength={120} onChange={e => setReason(e.target.value)} />
      {act.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(act.error)}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant={kind === 'ban' ? 'danger' : 'primary'} busy={act.isPending}>{kind === 'ban' ? 'Ban' : 'Kick'}</Button>
      </div>
    </form>
  )
}

function SayForm({ serverId }: { serverId: string }) {
  const [message, setMessage] = useState('')
  const say = useMutation({ mutationFn: () => api(`/api/servers/${serverId}/say`, { json: { message } }), onSuccess: () => setMessage('') })
  return (
    <form className="flex flex-wrap items-end gap-3 border-t border-line px-6 py-4" onSubmit={(e: FormEvent) => { e.preventDefault(); say.mutate() }}>
      <Field label="Message to everyone in game" className="min-w-0 flex-1" value={message} maxLength={200} onChange={e => setMessage(e.target.value)} />
      <Button type="submit" busy={say.isPending} disabled={!message.trim()}>Send</Button>
      {say.error && <p role="alert" className="m-0 w-full text-sm text-accent-text">{errorMessage(say.error)}</p>}
      {say.isSuccess && !message && <p role="status" className="m-0 w-full text-sm text-text-secondary">Sent.</p>}
    </form>
  )
}

function BansPanel({ serverId }: { serverId: string }) {
  const queryClient = useQueryClient()
  const bans = useQuery({ queryKey: ['bans', serverId], queryFn: () => api<BansDto>(`/api/servers/${serverId}/bans`) })
  const unban = useMutation({
    mutationFn: (b: { number: number; target: string }) => api(`/api/servers/${serverId}/bans/remove`, { json: b }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ['bans', serverId] }),
  })
  const d = bans.data
  return (
    <Panel title="Bans" className="md:col-span-6" meta={<Button size="sm" onClick={() => bans.refetch()}>Refresh</Button>}>
      {bans.isPending && <div className="px-6 py-5"><Loading /></div>}
      {d?.unavailable && <p className="m-0 px-6 py-5 text-text-secondary">The ban list isn't available: {d.unavailable}</p>}
      {d && !d.unavailable && d.bans.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No bans.</p>}
      {unban.error && <p role="alert" className="m-0 px-6 pt-3 text-sm text-accent-text">{errorMessage(unban.error)}</p>}
      <ul className="m-0 list-none p-0">
        {d?.bans.map(b => (
          <li key={`${b.number}-${b.target}`} className="flex flex-wrap items-center gap-x-4 gap-y-1 border-t border-line px-6 py-2.5 text-sm">
            <span className="font-mono text-xs">{b.kind === 'guid' ? `${b.target.slice(0, 8)}…` : b.target}</span>
            <span className="text-text-secondary">{b.minutesLeft === 'perm' ? 'permanent' : b.minutesLeft === '-' ? 'expired' : `${b.minutesLeft} min left`}</span>
            <span className="min-w-0 flex-1 break-words">{b.reason}</span>
            <Button size="sm" busy={unban.isPending && unban.variables?.number === b.number} disabled={unban.isPending} onClick={() => unban.mutate({ number: b.number, target: b.target })}>Unban</Button>
          </li>
        ))}
      </ul>
    </Panel>
  )
}

function RconLogPanel({ serverId, console: canCommand }: { serverId: string; console: boolean }) {
  const log = useQuery({ queryKey: ['rcon-log', serverId], queryFn: () => api<RconLine[]>(`/api/servers/${serverId}/rcon/log`), refetchInterval: 5000 })
  const [command, setCommand] = useState('')
  const run = useMutation({ mutationFn: () => api<{ text: string }>(`/api/servers/${serverId}/rcon`, { json: { command } }), onSuccess: () => void log.refetch() })
  return (
    <Panel title="RCon log" className="md:col-span-6" meta="Kept until the panel restarts">
      <div className="max-h-80 overflow-y-auto px-6 py-3 font-mono text-xs">
        {(log.data ?? []).length === 0 && <p className="m-0 text-text-muted">Nothing yet.</p>}
        {(log.data ?? []).slice().reverse().map((l, i) => (
          <div key={i} className="break-words"><span className="text-text-muted">{new Date(l.at).toLocaleTimeString()}</span> {l.text}</div>
        ))}
      </div>
      {canCommand && (
        <form className="flex flex-col gap-2 border-t border-line px-6 py-4" onSubmit={(e: FormEvent) => { e.preventDefault(); run.mutate() }}>
          <div className="flex flex-wrap items-end gap-3">
            <Field label="RCon command" className="min-w-0 flex-1" value={command} maxLength={220} onChange={e => setCommand(e.target.value)}
              hint="Allowed: players, bans, admins, missions, version, say, loadBans, writeBans, #lock, #unlock." />
            <Button type="submit" busy={run.isPending} disabled={!command.trim()}>Run</Button>
          </div>
          {run.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(run.error)}</p>}
          {run.data && <pre className="m-0 max-h-48 overflow-auto border border-line bg-console p-2 font-mono text-xs">{run.data.text || '(no reply)'}</pre>}
        </form>
      )}
    </Panel>
  )
}
