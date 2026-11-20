import { useQuery } from '@tanstack/react-query'
import { Outlet, useSearchParams } from 'react-router'
import { SubNav } from '../../components/SubNav'
import { api } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { ServerDto } from '../../lib/types'

export function ServerLayout() {
  const can = useCan()
  const [params] = useSearchParams()
  const p = params.get('p')
  const q = p ? `?p=${encodeURIComponent(p)}` : ''
  const tabs = [
    { to: `/server${q}`, label: 'Overview', end: true, show: can('server.view') },
    { to: `/server/players${q}`, label: 'Players', show: can('server.view') },
    { to: `/server/missions${q}`, label: 'Missions', show: can('missions.view') },
    { to: `/server/logs${q}`, label: 'Logs', show: can('logs.view') },
    { to: '/server/setup', label: 'Setup', show: can('agents.manage') || can('profiles.manage') },
  ].filter(t => t.show)
  return (
    <>
      {tabs.length > 1 && <SubNav label="Server sections" items={tabs} />}
      <Outlet />
    </>
  )
}

// The profiles the member can see, and the one picked with ?p= (the first by default).
export function useServers() {
  const can = useCan()
  const [params, setParams] = useSearchParams()
  const servers = useQuery({ queryKey: ['servers'], queryFn: () => api<ServerDto[]>('/api/servers'), enabled: can('server.view'), refetchInterval: 5000 })
  const wanted = params.get('p')
  const selected = servers.data?.find(s => s.id === wanted) ?? servers.data?.[0] ?? null
  return { servers, selected, select: (id: string) => setParams({ p: id }) }
}

export function ServerPicker({ servers, selected, onSelect }: { servers: ServerDto[]; selected: ServerDto; onSelect: (id: string) => void }) {
  if (servers.length < 2) return null
  return (
    <label className="flex items-center gap-2 text-sm text-text-muted">
      Server
      <select value={selected.id} onChange={e => onSelect(e.target.value)}
        className="min-h-9 border border-line-strong bg-console px-2 text-text pointer-coarse:min-h-11">
        {servers.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}
      </select>
    </label>
  )
}
