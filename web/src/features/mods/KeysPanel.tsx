import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Check, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { CollectionSummary, KeyDto } from '../../lib/types'

interface KeysResult { added: string[]; removed: string[]; backupFolder: string | null }

// The server's keys folder (Phase 5a Ruling 8): Start deploys a list's keys by itself; this is for checking and cleaning up.
export function KeysPanel() {
  const can = useCan()
  const installs = useQuery({ queryKey: ['mod-installs'], queryFn: () => api<{ id: string; name: string }[]>('/api/mods/installs') })
  const [chosen, setChosen] = useState('')
  const installId = chosen || installs.data?.[0]?.id
  const keys = useQuery({
    queryKey: ['keys', installId], enabled: !!installId,
    queryFn: () => api<{ keys: KeyDto[] }>(`/api/installs/${installId}/keys`).then(r => r.keys),
  })
  const lists = useQuery({ queryKey: ['collections'], queryFn: () => api<CollectionSummary[]>('/api/collections') })
  const [deployList, setDeployList] = useState('')
  const [cleaning, setCleaning] = useState(false)
  const queryClient = useQueryClient()
  const deploy = useMutation({
    mutationFn: () => api<KeysResult>(`/api/installs/${installId}/keys/deploy`, { json: { collectionId: deployList || lists.data?.[0]?.id } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['keys'] }),
  })
  if (installs.data?.length === 0) return null

  return (
    <Panel title="Server keys" meta="Start deploys a mod list's keys on its own">
      <div className="flex flex-col gap-4 px-6 py-5">
        {(installs.data?.length ?? 0) > 1 && (
          <SelectField label="Server install" value={installId} onChange={e => setChosen(e.target.value)} className="max-w-sm">
            {installs.data!.map(i => <option key={i.id} value={i.id}>{i.name}</option>)}
          </SelectField>
        )}
        {keys.isPending && <Loading />}
        {keys.error && <ErrorNote error={keys.error} onRetry={() => keys.refetch()} />}
        {keys.data && (
          <ul className="m-0 grid list-none grid-cols-1 gap-x-6 gap-y-1.5 p-0 text-sm md:grid-cols-2" aria-label="Keys in the server's keys folder">
            {keys.data.map(k => (
              <li key={k.name} className="flex min-w-0 flex-wrap items-center gap-2">
                <span className="font-mono">{k.name}</span>
                <StatusBlock tone={k.kind === 'mod' ? 'ok' : 'off'}>{k.kind === 'a3' ? 'Arma 3' : k.kind === 'mod' ? 'Mod' : 'Not a library mod'}</StatusBlock>
                {k.mods.length > 0 && <span className="truncate text-text-muted">{k.mods.join(', ')}</span>}
              </li>
            ))}
          </ul>
        )}
        {can('keys.manage') && !!lists.data?.length && (
          <div className="flex flex-wrap items-end gap-3 border-t border-line pt-4">
            <SelectField label="Deploy the keys of" value={deployList || lists.data[0].id} onChange={e => setDeployList(e.target.value)} className="min-w-56">
              {lists.data.map(l => <option key={l.id} value={l.id}>{l.name}</option>)}
            </SelectField>
            <Button busy={deploy.isPending} onClick={() => deploy.mutate()}>Deploy keys</Button>
            <Button variant="danger" onClick={() => setCleaning(true)}>Clean up keys…</Button>
          </div>
        )}
        {deploy.data && <p role="status" className="m-0 text-sm">{deploy.data.added.length ? `Added ${deploy.data.added.join(', ')}.` : 'Every key was already there.'}</p>}
        {deploy.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(deploy.error)}</p>}
      </div>
      <Dialog open={cleaning} onClose={() => setCleaning(false)} title="Clean up keys">
        {cleaning && installId && <Cleanup installId={installId} lists={lists.data ?? []} onDone={() => setCleaning(false)} />}
      </Dialog>
    </Panel>
  )
}

function Cleanup({ installId, lists, onDone }: { installId: string; lists: CollectionSummary[]; onDone: () => void }) {
  const queryClient = useQueryClient()
  const [keep, setKeep] = useState<Set<string>>(new Set(lists.filter(l => l.usedBy.length > 0).map(l => l.id)))
  const cleanup = useMutation({
    mutationFn: () => api<KeysResult>(`/api/installs/${installId}/keys/cleanup`, { json: { collectionIds: [...keep] } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['keys'] }),
  })
  if (cleanup.data)
    return (
      <div className="flex flex-col gap-4">
        <p className="m-0">{cleanup.data.removed.length ? `Removed ${cleanup.data.removed.join(', ')}.` : 'Nothing needed removing.'}</p>
        {cleanup.data.backupFolder && <p className="m-0 text-sm text-text-secondary">The keys folder was backed up first to <span className="font-mono">{cleanup.data.backupFolder}</span>.</p>}
        <Button className="self-end" onClick={onDone}>Done</Button>
      </div>
    )
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0">
        Keys of library mods that these lists don&apos;t use are removed, after the whole keys folder is backed up. Arma 3&apos;s and Creator DLC keys,
        and any key no library mod provides, always stay.
      </p>
      <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
        <legend className="sr-only">Mod lists to keep</legend>
        {lists.map(l => (
          <Check key={l.id} label={`${l.name}${l.usedBy.length ? ` (used by ${l.usedBy.join(', ')})` : ''}`} checked={keep.has(l.id)}
            onChange={() => setKeep(s => { const n = new Set(s); if (n.has(l.id)) n.delete(l.id); else n.add(l.id); return n })} />
        ))}
      </fieldset>
      {cleanup.error && <p role="alert" className="m-0 text-accent-text">{errorMessage(cleanup.error)}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="danger" disabled={keep.size === 0} busy={cleanup.isPending} onClick={() => cleanup.mutate()}>Remove the other keys</Button>
      </div>
    </div>
  )
}
