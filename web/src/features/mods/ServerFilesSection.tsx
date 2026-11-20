import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '../../components/Button'
import { SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { api, errorMessage } from '../../lib/api'
import { JobsPanel } from './JobsPanel'
import { useJobs } from './useJobs'

const branches = [
  { value: 'public', label: 'Public (stable)' },
  { value: 'creatordlc', label: 'Creator DLC (adds the CDLC server data)' },
  { value: 'profiling', label: 'Profiling (performance builds; may be unstable)' },
]

// SteamCMD installs or updates the dedicated server into an install folder (Phase 5a Ruling 15). It signs in as the Steam account
// on the Mods page; the server must be stopped.
export function ServerFilesSection() {
  const installs = useQuery({ queryKey: ['mod-installs'], queryFn: () => api<{ id: string; name: string }[]>('/api/mods/installs') })
  const { jobs, tails } = useJobs()
  const [branch, setBranch] = useState<Record<string, string>>({})
  const queryClient = useQueryClient()
  const run = useMutation({
    mutationFn: (id: string) => api(`/api/installs/${id}/steamcmd`, { json: { branch: branch[id] ?? 'public' } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  })
  return (
    <>
      <Panel title="3 · Server files" meta="Installed and updated through SteamCMD">
        <div className="flex flex-col gap-4 px-6 py-5">
          <p className="m-0 text-sm text-text-secondary">
            SteamCMD signs in as the Steam account set on the Mods page (it must own Arma 3) and downloads the dedicated server into the install&apos;s
            folder. Stop the servers on that install first.
          </p>
          {installs.data?.length === 0 && <p className="m-0 text-text-muted">Register an install above first.</p>}
          {installs.data?.map(i => (
            <div key={i.id} className="flex flex-wrap items-end gap-3">
              <SelectField label={`Branch for ${i.name}`} value={branch[i.id] ?? 'public'} onChange={e => setBranch(b => ({ ...b, [i.id]: e.target.value }))} className="min-w-72">
                {branches.map(b => <option key={b.value} value={b.value}>{b.label}</option>)}
              </SelectField>
              <Button variant="primary" busy={run.isPending && run.variables === i.id} onClick={() => run.mutate(i.id)}>Install or update {i.name}</Button>
            </div>
          ))}
          {run.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(run.error)}</p>}
        </div>
      </Panel>
      <JobsPanel jobs={jobs} tails={tails} types={['server.install']} />
    </>
  )
}
