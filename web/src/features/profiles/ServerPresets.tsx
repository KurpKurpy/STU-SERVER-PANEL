import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Button } from '../../components/Button'
import { Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { CfgKey, JsonValue, ServerPresetDto } from '../../lib/types'
import { useProfile } from './ProfileLayout'
import { useCatalog } from './useProfileDraft'

type Owned = 'basic' | 'launch'

// A launch flag that's off is the same as one that isn't set.
const norm = (k: CfgKey, v: JsonValue | undefined) => (v === undefined || (k.type === 'Flag' && v === false) ? undefined : v)

function show(k: CfgKey, v: JsonValue) {
  if (k.type === 'Flag') return 'On'
  if (typeof v !== 'number') return String(v)
  const text = v.toLocaleString('en-US')
  return /Bandwidth$/.test(k.name) ? `${text} (${(v / 1_000_000).toLocaleString('en-US', { maximumFractionDigits: 1 })} Mbit/s)` : text
}

// Performance presets on the Overview tab. Loading one copies its values into the draft (every performance key it doesn't
// set is cleared back to Arma's default); nothing else in the profile changes, and nothing reaches the server until Save and Apply.
export function ServerPresets() {
  const { draft } = useProfile()
  const can = useCan()
  const manage = can('profiles.manage')
  const queryClient = useQueryClient()
  const catalog = useCatalog()
  const presets = useQuery({ queryKey: ['server-presets'], queryFn: () => api<ServerPresetDto[]>('/api/server-presets') })
  const [loaded, setLoaded] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const owned = (catalog.data ?? []).filter(k => k.inServerPresets)
  const s = draft.snapshot!

  const current = (file: Owned) =>
    Object.fromEntries(owned.filter(k => k.file === file).flatMap(k => {
      const v = norm(k, s.settings[file][k.name])
      return v === undefined ? [] : [[k.name, v]]
    })) as Record<string, JsonValue>
  const matches = (p: ServerPresetDto) =>
    owned.every(k => JSON.stringify(norm(k, s.settings[k.file as Owned][k.name]) ?? null) === JSON.stringify(norm(k, p[k.file as Owned][k.name]) ?? null))

  function load(p: ServerPresetDto) {
    draft.update(x => {
      for (const k of owned) {
        const values = x.settings[k.file as Owned]
        const v = norm(k, p[k.file as Owned][k.name])
        if (v === undefined) delete values[k.name]
        else values[k.name] = v
      }
    })
    setLoaded(p.name)
  }

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['server-presets'] })
  const create = useMutation({
    mutationFn: () => api<ServerPresetDto>('/api/server-presets', { json: { name, description, basic: current('basic'), launch: current('launch') } }),
    onSuccess: async () => { setName(''); setDescription(''); await refresh() },
  })
  const update = useMutation({
    mutationFn: (p: ServerPresetDto) => api(`/api/server-presets/${p.id}`, {
      method: 'PUT', json: { name: p.name, description: p.description, basic: current('basic'), launch: current('launch') },
    }),
    onSuccess: refresh,
  })
  const remove = useMutation({ mutationFn: (id: string) => api(`/api/server-presets/${id}`, { method: 'DELETE' }), onSuccess: refresh })

  function submit(e: FormEvent) {
    e.preventDefault()
    create.mutate()
  }

  const error = [update.error, remove.error].find(Boolean)
  return (
    <Panel title="Server presets" meta="Performance only">
      <div className="flex flex-col gap-4 px-6 py-5">
        <p className="m-0 text-sm text-text-secondary">
          Network tuning (basic.cfg) and the startup options that make the server run better. Loading a preset changes only those
          settings, never players, timeouts, kick rules, passwords or missions. Check the <Link to="performance">Performance</Link> and{' '}
          <Link to="startup">Startup</Link> tabs, then save and apply.
        </p>
        {loaded && <p role="status" className="m-0 border border-ok/40 bg-black/20 px-4 py-3 text-sm">Loaded {loaded} into your unsaved changes.</p>}
        {presets.isError && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(presets.error)}</p>}
        {error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(error)}</p>}
        <ul className="m-0 grid list-none grid-cols-1 gap-3 p-0 md:grid-cols-2 2xl:grid-cols-3" aria-label="Presets">
          {(presets.data ?? []).map(p => {
            const inUse = catalog.data !== undefined && matches(p)
            const rows = owned.flatMap(k => {
              const v = norm(k, p[k.file as Owned][k.name])
              return v === undefined ? [] : [[k, v] as const]
            })
            return (
              <li key={p.id} className="flex min-w-0 flex-col gap-3 border border-line bg-console px-4 py-3">
                <div className="flex flex-wrap items-center gap-2">
                  <h3 className="m-0 text-base font-semibold text-text">{p.name}</h3>
                  {inUse && <StatusBlock tone="ok">In use</StatusBlock>}
                  {!p.isBuiltIn && <StatusBlock tone="off">Yours</StatusBlock>}
                </div>
                {p.description && <p className="m-0 text-sm text-text-muted">{p.description}</p>}
                {rows.length === 0 ? <p className="m-0 text-sm text-text-muted">Nothing set: Arma's defaults.</p> : (
                  <dl className="m-0 grid grid-cols-[minmax(0,1fr)_auto] gap-x-3 gap-y-1 text-sm">
                    {rows.map(([k, v]) => (
                      <div key={k.name} className="contents">
                        <dt className="min-w-0 text-text-muted">{k.label}</dt>
                        <dd className="m-0 text-right font-mono text-text-secondary">{show(k, v)}</dd>
                      </div>
                    ))}
                  </dl>
                )}
                {manage && (
                  <div className="mt-auto flex flex-wrap gap-2">
                    <Button size="sm" variant={inUse ? 'default' : 'primary'} disabled={inUse} onClick={() => load(p)}>Load</Button>
                    {!p.isBuiltIn && <Button size="sm" busy={update.isPending && update.variables?.id === p.id} onClick={() => update.mutate(p)}>Update from this profile</Button>}
                    {!p.isBuiltIn && <Button size="sm" variant="danger" busy={remove.isPending && remove.variables === p.id} onClick={() => remove.mutate(p.id)}>Delete</Button>}
                  </div>
                )}
              </li>
            )
          })}
        </ul>
        <details className="border border-line px-4 py-3 text-sm">
          <summary className="cursor-pointer font-semibold text-text-secondary">Tuned for your server, and what no preset can fix</summary>
          <p className="mb-0 mt-2 text-text-muted">
            The presets are built for the unit's host: AMD EPYC 4344P (8 cores, 16 threads), 64 GB of memory, Windows Server 2025, with
            a 1 Gbit/s uplink. Arma finds the 8 cores and sizes its memory itself, so CPU count, extra threads, all logical cores
            and max memory stay unset. Huge pages need the Windows "Lock pages in memory" right for the account that runs the server; grant
            it before trying them on the Startup tab. -setThreadCharacteristics can freeze Windows Server, so the panel never passes it.
          </p>
          <p className="mb-0 mt-2 text-text-muted">
            Presets remove network and engine bottlenecks, but they can't add CPU. If <code>#monitor</code> shows server FPS below 20
            during an op, the mission is the cause: too many active AI, AI left on the server or the Zeus player's PC instead of
            headless clients, or heavy scripts. On this 8-core box, run two headless clients (three at most) with ACE's headless transfer
            or another transfer module, check that Zeus-spawned AI actually moves to them, and use Dynamic Simulation and garbage collection. Change one
            value at a time and compare <code>#monitor</code> under similar load.
          </p>
        </details>
        {manage && (
          <form className="flex flex-col gap-3 border-t border-line pt-4" onSubmit={submit} aria-label="Save as a preset">
            <h3 className="m-0 text-sm font-semibold text-text-secondary">Save this profile's tuning as a preset</h3>
            <div className="grid grid-cols-1 gap-3 md:grid-cols-[minmax(0,1fr)_minmax(0,2fr)]">
              <Field label="Name" value={name} maxLength={40} required onChange={e => setName(e.target.value)} />
              <Field label="Description" value={description} maxLength={200} placeholder="For example: our 1 Gbit box, 8 cores"
                onChange={e => setDescription(e.target.value)} />
            </div>
            <p className="m-0 text-xs text-text-muted">Saves the performance values on this profile now, unsaved changes included.</p>
            {create.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(create.error)}</p>}
            <Button type="submit" variant="primary" className="self-start" busy={create.isPending}>Save as preset</Button>
          </form>
        )}
      </div>
    </Panel>
  )
}
