import { useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Outlet, useOutletContext, useParams } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { useCan } from '../../lib/auth'
import { ApplyDialog } from './ApplyDialog'
import { ErrorNote, Loading } from '../../components/Feedback'
import { StatusBlock } from '../../components/StatusBlock'
import { SubNav } from '../../components/SubNav'
import { api } from '../../lib/api'
import type { ProfileSummary, ServerDto } from '../../lib/types'
import { SaveBar } from './SaveBar'
import { useProfileDraft, type ProfileDraft } from './useProfileDraft'

export function appliedState(p: Pick<ProfileSummary, 'revision' | 'appliedRevision' | 'needsCfg'>) {
  if (p.needsCfg) return { tone: 'bad' as const, label: 'Needs server.cfg' }
  if (p.appliedRevision === null) return { tone: 'off' as const, label: 'Never applied' }
  if (p.appliedRevision < p.revision) return { tone: 'warn' as const, label: 'Changes not applied' }
  return { tone: 'ok' as const, label: `Applied r${p.appliedRevision}` }
}

export interface ProfileContext { draft: ProfileDraft; running: boolean; agentConnected: boolean }
export const useProfile = () => useOutletContext<ProfileContext>()

export function ProfileLayout() {
  const { id = '' } = useParams()
  const can = useCan()
  const [applying, setApplying] = useState(false)
  const draft = useProfileDraft(id)
  const servers = useQuery({ queryKey: ['servers'], queryFn: () => api<ServerDto[]>('/api/servers'), refetchInterval: 5000 })
  const server = servers.data?.find(s => s.id === id)
  const running = !!server && !['offline', 'error'].includes(server.state)
  const agentConnected = !!server && server.state !== 'unknown'

  if (draft.detail.isPending) return <Loading />
  if (draft.detail.error) return <ErrorNote error={draft.detail.error} onRetry={() => draft.detail.refetch()} />
  const p = draft.detail.data
  const state = appliedState(p)
  const base = `/profiles/${id}`
  const tabs = [
    { to: base, label: 'Overview', end: true },
    { to: `${base}/general`, label: 'General' },
    { to: `${base}/server-config`, label: 'Server config' },
    { to: `${base}/performance`, label: 'Performance' },
    { to: `${base}/difficulty`, label: 'Difficulty' },
    { to: `${base}/missions`, label: 'Missions' },
    { to: `${base}/startup`, label: 'Startup' },
    { to: `${base}/headless`, label: 'Headless clients' },
    { to: `${base}/mods`, label: 'Mods' },
    { to: `${base}/revisions`, label: 'Revisions' },
  ]

  return (
    <div className="flex min-w-0 flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="display m-0 text-[30px]">{p.name}</h2>
        <span className="font-mono text-sm text-text-muted">{p.slug} · r{p.revision}</span>
        <StatusBlock tone={state.tone}>{state.label}</StatusBlock>
        {can('profiles.apply') && (
          <Button variant="primary" className="ml-auto" disabled={draft.dirty} title={draft.dirty ? 'Save your changes first' : undefined}
            onClick={() => setApplying(true)}>Apply to server</Button>
        )}
      </div>
      <Dialog open={applying} onClose={() => setApplying(false)} title="Apply to the server">
        {applying && <ApplyDialog profileId={id} onDone={() => setApplying(false)} />}
      </Dialog>
      <SubNav label="Profile sections" items={tabs} />
      <Outlet context={{ draft, running, agentConnected } satisfies ProfileContext} />
      <SaveBar draft={draft} />
    </div>
  )
}
