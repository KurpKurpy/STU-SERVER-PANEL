import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Check, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { ApiError, api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { ServerDto, ServerMissionRow, ServerMissionsDto, StartMissionPreview } from '../../lib/types'
import { CompatChip, CompatReasons } from '../missions/Compat'
import { NoServerYet } from './ServerPage'
import { ServerPicker, useServers } from './ServerLayout'

const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-3 align-top'

// Phase 5b (Amendment A): the missions on this server that a mod list can run, and Start Mission for Zeus and mission makers.
export function ServerMissionsPage() {
  const can = useCan()
  const { servers, selected, select } = useServers()
  const [listId, setListId] = useState<string | null>(null)
  const [showAll, setShowAll] = useState(false)
  const [starting, setStarting] = useState<ServerMissionRow | null>(null)
  const missions = useQuery({
    queryKey: ['server-missions', selected?.id, listId],
    queryFn: () => api<ServerMissionsDto>(`/api/servers/${selected!.id}/missions${listId ? `?modListId=${listId}` : ''}`),
    enabled: !!selected,
  })

  if (servers.isPending) return <Loading />
  if (servers.error && !servers.data) return <ErrorNote error={servers.error} onRetry={() => servers.refetch()} />
  if (!selected) return <NoServerYet />
  const data = missions.data
  const chosen = data?.modListId ?? null
  const rows = (data?.missions ?? []).filter(m => showAll || m.status !== 'Missing')
  const hidden = (data?.missions.length ?? 0) - rows.length

  return (
    <>
      <Panel title="Missions on the server" meta={<ServerPicker servers={servers.data} selected={selected} onSelect={id => { setListId(null); select(id) }} />}>
        <div className="flex flex-wrap items-end gap-x-6 gap-y-3 px-6 py-4">
          <SelectField label="Mod list" value={chosen ?? ''} onChange={e => setListId(e.target.value || null)} className="min-w-56" disabled={!data}>
            {!chosen && <option value="">No mod list</option>}
            {data?.modLists.map(l => <option key={l.id} value={l.id}>{l.name}{l.id === data.appliedModListId ? ' (on the server)' : ''}</option>)}
          </SelectField>
          <Check label="Show missions that don't match" checked={showAll} onChange={e => setShowAll(e.target.checked)} />
        </div>
        {!!data?.notInstalled.length && (
          <p role="status" className="m-0 mx-6 mb-4 border border-warn/60 px-4 py-3 text-sm">
            This list has mods that aren't installed: <strong>{data.notInstalled.join(', ')}</strong>. Install them on the <Link to="/mods">Mods</Link> page before starting a mission with it.
          </p>
        )}
        <p className="m-0 px-6 pb-4 text-sm text-text-secondary">
          A mission is listed when this mod list and the server provide every addon its mission.sqm needs, and its map. Extra mods don't hide anything.
        </p>
        {missions.isPending && <div className="px-6 pb-5"><Loading /></div>}
        {missions.error && <div className="px-6 pb-5"><ErrorNote error={missions.error} onRetry={() => missions.refetch()} /></div>}
        {data && rows.length === 0 && (
          <p className="m-0 border-t border-line px-6 py-5 text-text-muted">
            {data.missions.length === 0 ? <>No library missions are on this server yet. <Link to="/missions">Upload one</Link> and it's written here.</> : 'No mission matches this mod list.'}
          </p>
        )}
        {rows.length > 0 && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-sm">
              <thead><tr><th className={th}>Mission</th><th className={th}>Version</th><th className={th}>Status</th><th className={th}><span className="sr-only">Start</span></th></tr></thead>
              <tbody>
                {rows.map(m => (
                  <tr key={m.missionId}>
                    <td className={td}>
                      <Link to={`/missions/${m.missionId}`} className="font-semibold text-text hover:text-accent-text">{m.displayName}</Link>
                      <div className="break-all font-mono text-xs text-text-muted">{m.fileName}</div>
                    </td>
                    <td className={`${td} tabular-nums`}>v{m.version}</td>
                    <td className={td}><CompatChip status={m.status} /><CompatReasons r={m} /></td>
                    <td className={`${td} text-right`}>
                      {can('missions.start') && m.status !== 'Missing' && chosen && !data?.notInstalled.length && (
                        <Button size="sm" variant="primary" onClick={() => setStarting(m)}>Start mission</Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {hidden > 0 && <p className="m-0 border-t border-line px-6 py-3 text-xs text-text-muted">{hidden} mission{hidden === 1 ? '' : 's'} hidden: they don't match this mod list.</p>}
      </Panel>
      <Dialog open={!!starting} onClose={() => setStarting(null)} title="Start mission">
        {starting && chosen && <StartConfirm server={selected} mission={starting} listId={chosen} onDone={() => setStarting(null)} />}
      </Dialog>
    </>
  )
}

// What will happen, from the panel's own preview, then the one confirmed action.
function StartConfirm({ server, mission, listId, onDone }: { server: ServerDto; mission: ServerMissionRow; listId: string; onDone: () => void }) {
  const queryClient = useQueryClient()
  const preview = useQuery({
    queryKey: ['start-preview', server.id, mission.missionId, listId],
    queryFn: () => api<StartMissionPreview>(`/api/servers/${server.id}/start-mission?missionId=${mission.missionId}&modListId=${listId}`),
    staleTime: 0,
  })
  const start = useMutation({
    mutationFn: (players: number | null) => api(`/api/servers/${server.id}/start-mission`, { json: { missionId: mission.missionId, modListId: listId, players } }),
    onSuccess: async () => {
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['servers'] }), queryClient.invalidateQueries({ queryKey: ['server-missions'] })])
      onDone()
    },
    onError: e => { if (e instanceof ApiError && e.code === 'stale_players') preview.refetch() },
  })
  if (preview.isPending) return <Loading />
  if (preview.error) return <ErrorNote error={preview.error} onRetry={() => preview.refetch()} />
  const p = preview.data
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-text-secondary"><strong className="text-text">{mission.displayName}</strong> on {server.name}:</p>
      <ul className="m-0 flex list-disc flex-col gap-1.5 pl-5 text-sm">
        {p.action === 'restart'
          ? <li><strong>Restarts the server</strong>{p.players ? `; ${p.players} player${p.players === 1 ? '' : 's'} will be disconnected` : ''}.</li>
          : <li>Starts the server.</li>}
        <li>Sets the mission rotation to {p.fileName.replace(/\.pbo$/i, '')} only.</li>
        {p.modListChanges && <li>Switches the mod list to <strong>{p.modListName}</strong>.</li>}
        {p.unappliedChanges && <li>Also applies changes saved on the profile since it was last applied.</li>}
        {p.compat.status === 'Unknown' && <li className="text-warn">The panel couldn't confirm every addon this mission needs: {p.compat.unreadableMods.join(', ')} couldn't be fully read.</li>}
      </ul>
      {start.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(start.error)}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="primary" busy={start.isPending} onClick={() => start.mutate(p.players)}>{p.action === 'restart' ? 'Restart with this mission' : 'Start with this mission'}</Button>
      </div>
    </div>
  )
}
