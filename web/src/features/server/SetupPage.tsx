import { PlusIcon } from '@phosphor-icons/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { CopyField } from '../../components/CopyField'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import { Link } from 'react-router'
import type { AgentCreated, AgentDto, AreaInfo, InstallDto } from '../../lib/types'
import { ServerFilesSection } from '../mods/ServerFilesSection'
import { ScanChip } from '../missions/Compat'
import { RescanButton } from '../mods/ModsPage'

const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-2.5'

function useAgents(enabled: boolean) {
  // Polling while any agent is offline is the connection test: it flips to Connected as soon as the agent dials in.
  return useQuery({
    queryKey: ['agents'],
    queryFn: () => api<AgentDto[]>('/api/agents'),
    enabled,
    refetchInterval: q => (q.state.data?.some(a => !a.connected) ? 3000 : false),
  })
}

function useInstalls(enabled: boolean) {
  return useQuery({ queryKey: ['installs'], queryFn: () => api<InstallDto[]>('/api/installs'), enabled })
}

export function ServerSetupPage() {
  const can = useCan()
  if (!can('agents.manage') && !can('profiles.manage') && !can('server.update')) return <NoAccess />
  return (
    <div className="stagger flex flex-col gap-6">
      {can('agents.manage') && <AgentSection />}
      {can('agents.manage') && <InstallSection />}
      {can('server.update') && can('mods.view') && <ServerFilesSection />}
      {can('profiles.manage') && <ProfileSection />}
    </div>
  )
}

function KeyNotice({ name, secret, onClose }: { name: string; secret: string; onClose: () => void }) {
  return (
    <Panel title={`Key for ${name}`} emphasis meta={<Button size="sm" onClick={onClose}>Done</Button>}>
      <div className="flex flex-col gap-4 px-6 py-5">
        <p className="m-0 font-semibold">Copy this key now. It is shown once and can't be read back.</p>
        <CopyField value={secret} label="agent key" />
        <ol className="m-0 flex max-w-prose flex-col gap-2 pl-5 text-text-secondary">
          <li>On the server box, open the agent's <code className="font-mono">appsettings.json</code> and paste the key into <code className="font-mono">Agent:Key</code>.</li>
          <li>Set <code className="font-mono">Agent:PanelUrl</code> to this panel's address, <code className="font-mono">{window.location.origin}</code>.</li>
          <li>Run <code className="font-mono">ConsoleAgent.exe</code>. The agent shows as Connected here within a few seconds.</li>
        </ol>
      </div>
    </Panel>
  )
}

function AgentSection() {
  const agents = useAgents(true)
  const queryClient = useQueryClient()
  const [shown, setShown] = useState<{ name: string; key: string } | null>(null)
  const [rotating, setRotating] = useState<AgentDto | null>(null)
  const [error, setError] = useState<string | null>(null)

  const add = useMutation({
    mutationFn: (name: string) => api<AgentCreated>('/api/agents', { json: { name } }),
    onSuccess: async created => { setShown({ name: created.name, key: created.key }); await queryClient.invalidateQueries({ queryKey: ['agents'] }) },
    onError: err => setError(errorMessage(err)),
  })
  const rotate = useMutation({
    mutationFn: (a: AgentDto) => api<{ key: string }>(`/api/agents/${a.id}/key`, { method: 'POST' }),
    onSuccess: async (r, a) => { setRotating(null); setShown({ name: a.name, key: r.key }); await queryClient.invalidateQueries({ queryKey: ['agents'] }) },
  })

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    setError(null)
    const form = e.currentTarget
    add.mutate(String(new FormData(form).get('name') ?? ''), { onSuccess: () => form.reset() })
  }

  return (
    <>
      {shown && <KeyNotice name={shown.name} secret={shown.key} onClose={() => setShown(null)} />}
      <Panel title="1 · Server agent" meta="Runs on the server box and dials out to this panel">
        {agents.isPending && <div className="px-6 py-5"><Loading /></div>}
        {agents.error && <div className="px-6 py-5"><ErrorNote error={agents.error} onRetry={() => agents.refetch()} /></div>}
        {agents.data && agents.data.length > 0 && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse">
              <thead><tr><th className={th}>Agent</th><th className={th}>Status</th><th className={th}>Machine</th><th className={th}>Version</th><th className={th}>Last seen</th><th className={th}><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {agents.data.map(a => (
                  <tr key={a.id}>
                    <td className={`${td} font-semibold`}>{a.name}</td>
                    <td className={td}>{a.connected ? <StatusBlock tone="ok">Connected</StatusBlock> : <StatusBlock tone="off">Offline</StatusBlock>}</td>
                    <td className={`${td} font-mono text-sm text-text-muted`}>{a.machineName ?? '—'}</td>
                    <td className={`${td} font-mono text-sm text-text-muted`}>{a.version ?? '—'}</td>
                    <td className={`${td} whitespace-nowrap font-mono text-sm text-text-muted`}>{a.connected ? 'now' : formatWhen(a.lastSeenAt)}</td>
                    <td className={`${td} text-right`}><Button size="sm" onClick={() => setRotating(a)}>New key</Button></td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <form onSubmit={submit} className="flex flex-wrap items-end gap-3 px-6 py-5">
          <Field label="Add an agent" name="name" placeholder="e.g. Server box" maxLength={40} required className="min-w-56 flex-1" error={error ?? undefined} />
          <Button type="submit" busy={add.isPending}><PlusIcon size={14} weight="bold" aria-hidden="true" />Add agent</Button>
        </form>
      </Panel>
      <Dialog open={rotating !== null} onClose={() => setRotating(null)} title="Issue a new key?">
        {rotating && (
          <>
            <p className="m-0 max-w-prose text-text-secondary">
              {rotating.name} is disconnected at once and its old key stops working. The server keeps running; the agent reconnects once the new key is in its appsettings.json and it is restarted.
            </p>
            {rotate.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(rotate.error)}</p>}
            <div className="flex justify-end gap-2.5">
              <Button onClick={() => setRotating(null)}>Cancel</Button>
              <Button variant="primary" busy={rotate.isPending} onClick={() => rotate.mutate(rotating)}>Issue new key</Button>
            </div>
          </>
        )}
      </Dialog>
    </>
  )
}

const areaNames: Record<string, string> = { profiles: 'Profiles and logs', configs: 'Generated configs', backups: 'Backups', workshop: 'Workshop mods', localmods: 'Local mods' }
const areaName = (a: AreaInfo) => areaNames[a.area] ?? 'Server install'

function InstallSection() {
  const can = useCan()
  const agents = useAgents(true)
  const installs = useInstalls(can('profiles.manage'))
  const connected = agents.data?.filter(a => a.connected) ?? []

  return (
    <Panel title="2 · Server install" meta="Paths are the agent's own; this page only shows them">
      {agents.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">Add an agent above first. Its installs and folder checks appear once it connects.</p>}
      {agents.data && agents.data.length > 0 && connected.length === 0 && (
        <p className="m-0 px-6 py-5 text-text-muted">No agent is connected yet. Start the agent with its key; its installs and folder checks appear here once it connects.</p>
      )}
      {connected.map(a => (
        <div key={a.id} className="flex flex-col gap-4 border-b border-line px-6 py-5 last:border-b-0">
          <h3 className="m-0 text-lg font-semibold">{a.name}</h3>
          {a.installs.length === 0 ? (
            <p className="m-0 max-w-prose text-text-secondary">
              The agent found no Arma 3 server. Add its folder to <code className="font-mono">Agent:InstallRoots</code> in the agent's appsettings.json and restart the agent.
            </p>
          ) : (
            <ul className="m-0 flex list-none flex-col gap-3 p-0">
              {a.installs.map(i => (
                <InstallRow key={i.path} agentId={a.id} path={i.path} hasExe={i.hasServerExe}
                  registered={installs.data?.find(r => r.agentId === a.id && r.installPath.toLowerCase() === i.path.toLowerCase())} />
              ))}
            </ul>
          )}
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse">
              <thead><tr><th className={th}>Folder</th><th className={th}>Path</th><th className={th}>Exists</th><th className={th}>Access</th></tr></thead>
              <tbody>
                {a.areas.map(area => (
                  <tr key={area.area}>
                    <td className={`${td} whitespace-nowrap`}>{areaName(area)}</td>
                    <td className={`${td} break-all font-mono text-sm`}>{area.path}</td>
                    <td className={td}>{area.exists ? <StatusBlock tone="ok">Yes</StatusBlock> : <StatusBlock tone="bad">Missing</StatusBlock>}</td>
                    <td className={td}>
                      {area.readOnly ? <StatusBlock tone="off">Read only</StatusBlock>
                        : area.writable ? <StatusBlock tone="ok">Writable</StatusBlock> : <StatusBlock tone="bad">Not writable</StatusBlock>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="m-0 max-w-prose text-sm text-text-muted">
            To change a folder, edit <code className="font-mono">Agent:DataRoot</code> or <code className="font-mono">Agent:InstallRoots</code> in the agent's appsettings.json on the server box and restart the agent. The panel can't change them, so a compromised panel can't redirect where the agent writes.
          </p>
        </div>
      ))}
    </Panel>
  )
}

function InstallRow({ agentId, path, hasExe, registered }: { agentId: string; path: string; hasExe: boolean; registered?: InstallDto }) {
  const queryClient = useQueryClient()
  const can = useCan()
  const [name, setName] = useState('Arma 3')
  const use = useMutation({
    mutationFn: () => api('/api/installs', { json: { agentId, path, name } }),
    onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ['installs'] }), queryClient.invalidateQueries({ queryKey: ['mod-installs'] })]),
  })
  return (
    <li className="flex flex-wrap items-end gap-3 border border-line px-4 py-3">
      <div className="flex min-w-0 flex-1 flex-col gap-1.5">
        <span className="break-all font-mono text-sm">{path}</span>
        <span>{hasExe ? <StatusBlock tone="ok">Server exe found</StatusBlock> : <StatusBlock tone="warn">No server yet: install it in step 3</StatusBlock>}</span>
      </div>
      {registered ? (
        <span className="flex flex-wrap items-center gap-2 text-sm text-text-secondary">
          Registered as <strong>{registered.name}</strong>
          <ScanChip status={registered.scanStatus} note={registered.scanNote} />
          {can('server.update') && <RescanButton path={`/api/installs/${registered.id}/scan`} invalidate="installs" />}
        </span>
      ) : (
        <form onSubmit={e => { e.preventDefault(); use.mutate() }} className="flex flex-wrap items-end gap-2">
          <Field label="Install name" value={name} onChange={e => setName(e.target.value)} maxLength={40} required
            error={use.error ? errorMessage(use.error) : undefined} />
          <Button type="submit" busy={use.isPending}>Use this install</Button>
        </form>
      )}
    </li>
  )
}

// Phase 3: profiles live in their own section.
function ProfileSection() {
  return (
    <Panel title="4 · Profile">
      <p className="m-0 px-6 py-5 text-text-secondary">
        Profiles are set up in <Link to="/profiles">Profiles</Link>: settings, revisions and Apply.
      </p>
    </Panel>
  )
}
