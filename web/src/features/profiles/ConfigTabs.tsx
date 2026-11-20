import { ArrowDownIcon, ArrowUpIcon, ArrowsClockwiseIcon, XIcon } from '@phosphor-icons/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link } from 'react-router'
import { Button } from '../../components/Button'
import { CopyField } from '../../components/CopyField'
import { Dialog } from '../../components/Dialog'
import { Check, Field, SelectField, TextArea } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { MissionsList, PresetDto, RotationItem, ServerDto } from '../../lib/types'
import { ConfigPreview } from './ConfigPreview'
import { useProfile } from './ProfileLayout'
import { SettingsForm } from './SettingsForm'

const difficulties = ['Recruit', 'Regular', 'Veteran', 'Custom']

export function MissionsTab() {
  const { draft } = useProfile()
  const can = useCan()
  const readOnly = !can('profiles.manage')
  const p = draft.detail.data!
  const s = draft.snapshot!
  const missions = useQuery({ queryKey: ['missions', p.id], queryFn: () => api<MissionsList>(`/api/profiles/${p.id}/missions`) })
  const servers = useQuery({ queryKey: ['servers'], queryFn: () => api<ServerDto[]>('/api/servers'), refetchInterval: 5000 })
  const server = servers.data?.find(x => x.id === p.id)
  const [typed, setTyped] = useState('')
  const onServer = new Set((missions.data?.files ?? []).map(f => f.name.replace(/\.pbo$/i, '').toLowerCase()))
  const override = s.settings.missionsOverride !== null

  const edit = (change: (r: RotationItem[]) => void) => draft.update(x => change(x.rotation))
  const add = (template: string) => template.trim() && edit(r => { r.push({ template: template.trim(), difficulty: 'Custom', paramsText: '' }) })
  const move = (i: number, by: number) => edit(r => { const [item] = r.splice(i, 1); r.splice(i + by, 0, item) })

  // Now and next: A2S names the mission, the RPT gives its template (spike F15/F17).
  const running = server && ['online', 'starting', 'notResponding'].includes(server.state)
  const at = server?.missionTemplate ? s.rotation.findIndex(r => r.template.toLowerCase() === server.missionTemplate!.toLowerCase()) : -1
  const random = s.settings.server.randomMissionOrder === true
  const next = random ? 'Random order' : at >= 0 && s.rotation.length > 0 ? s.rotation[(at + 1) % s.rotation.length].template : '—'

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <Panel title="Mission rotation" meta={<Button size="sm" onClick={() => missions.refetch()}><ArrowsClockwiseIcon size={14} aria-hidden="true" />Refresh</Button>}>
        <div className="flex flex-col gap-5 px-6 py-5">
          <dl className="m-0 grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div><dt className="text-2xs font-semibold uppercase tracking-label text-text-muted">Now running</dt>
              <dd className="m-0">{running ? server.missionTemplate ?? server.query?.mission ?? '—' : 'Not running'}</dd></div>
            <div><dt className="text-2xs font-semibold uppercase tracking-label text-text-muted">Next in rotation</dt>
              <dd className="m-0">{running ? next : '—'}</dd></div>
          </dl>
          {!missions.data?.connected && !missions.isPending && <p className="m-0 text-sm text-text-muted">The agent isn't connected, so the mission list can't be read.</p>}
          <Check label="Use a hand-written Missions class" disabled={readOnly} checked={override}
            hint="For campaigns or entries the list can't show; the list below is ignored while this is on."
            onChange={e => draft.update(x => { x.settings.missionsOverride = e.target.checked ? (x.settings.missionsOverride ?? 'class Missions\r\n{\r\n};\r\n') : null })} />
          {override ? (
            <TextArea label="class Missions" rows={12} spellCheck={false} disabled={readOnly} value={s.settings.missionsOverride ?? ''}
              error={draft.fieldErrors.missionsOverride} className="[&_textarea]:font-mono [&_textarea]:text-sm"
              onChange={e => draft.update(x => { x.settings.missionsOverride = e.target.value })} />
          ) : (
            <>
              {s.rotation.length === 0 && <p className="m-0 text-text-muted">No missions in the rotation.</p>}
              <ol className="m-0 flex list-none flex-col gap-3 p-0">
                {s.rotation.map((r, i) => (
                  <li key={i} className="flex flex-col gap-2 border border-line bg-black/20 p-3">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="min-w-0 flex-1 font-mono text-sm break-all">{i + 1}. {r.template}</span>
                      {missions.data?.connected && !onServer.has(r.template.toLowerCase()) && <StatusBlock tone="warn">Not in mpmissions</StatusBlock>}
                      {!readOnly && (
                        <span className="flex gap-1">
                          <Button size="sm" aria-label={`Move ${r.template} up`} disabled={i === 0} onClick={() => move(i, -1)}><ArrowUpIcon size={14} aria-hidden="true" /></Button>
                          <Button size="sm" aria-label={`Move ${r.template} down`} disabled={i === s.rotation.length - 1} onClick={() => move(i, 1)}><ArrowDownIcon size={14} aria-hidden="true" /></Button>
                          <Button size="sm" variant="danger" aria-label={`Remove ${r.template}`} onClick={() => edit(x => { x.splice(i, 1) })}><XIcon size={14} aria-hidden="true" /></Button>
                        </span>
                      )}
                    </div>
                    <SelectField label="Difficulty" value={r.difficulty} disabled={readOnly} onChange={e => edit(x => { x[i].difficulty = e.target.value })}>
                      {[...new Set([...difficulties, r.difficulty])].map(d => <option key={d} value={d}>{d}</option>)}
                    </SelectField>
                    <details>
                      <summary className="cursor-pointer text-sm text-text-muted">Mission parameters{r.paramsText.trim() ? ' (set)' : ''}</summary>
                      <TextArea label="Parameters (name = value; per line)" rows={4} spellCheck={false} disabled={readOnly} value={r.paramsText}
                        error={draft.fieldErrors[`rotation.${i}.paramsText`]} className="mt-2 [&_textarea]:font-mono [&_textarea]:text-sm"
                        onChange={e => edit(x => { x[i].paramsText = e.target.value })} />
                    </details>
                    {draft.fieldErrors[`rotation.${i}.template`] && <p role="alert" className="m-0 text-sm text-accent-text">{draft.fieldErrors[`rotation.${i}.template`]}</p>}
                  </li>
                ))}
              </ol>
              {!readOnly && (
                <div className="flex flex-col gap-3">
                  <SelectField label="Add a mission from the server" value="" disabled={!missions.data?.files.length}
                    hint={missions.data?.connected && !missions.data.files.length ? 'No .pbo files in the server\'s mpmissions folder.' : undefined}
                    onChange={e => add(e.target.value)}>
                    <option value="">{missions.isPending ? 'Looking…' : 'Choose a mission'}</option>
                    {missions.data?.files.map(f => <option key={f.name} value={f.name.replace(/\.pbo$/i, '')}>{f.name}</option>)}
                  </SelectField>
                  <form className="flex flex-wrap items-end gap-2" onSubmit={e => { e.preventDefault(); add(typed); setTyped('') }}>
                    <Field label="Or type a name" value={typed} placeholder="MP_CombatPatrol_01.Altis" className="min-w-0 flex-1 [&_input]:font-mono"
                      hint="For missions that come from an addon: class name and terrain." onChange={e => setTyped(e.target.value)} />
                    <Button type="submit" disabled={!typed.trim()}>Add</Button>
                  </form>
                </div>
              )}
            </>
          )}
          <SettingsForm file="server" tab="missions" values={s.settings.server} errors={draft.fieldErrors} readOnly={readOnly}
            onChange={(k, v) => draft.setValue('server', k, v)} />
        </div>
      </Panel>
      <Panel title="server.cfg">
        <div className="px-6 py-5">
          <ConfigPreview title="Generated server.cfg" file="server.cfg" text={draft.preview?.files.serverCfg} draft={draft} />
        </div>
      </Panel>
    </div>
  )
}

export function DifficultyTab() {
  const { draft } = useProfile()
  const can = useCan()
  const queryClient = useQueryClient()
  const readOnly = !can('profiles.manage')
  const s = draft.snapshot!
  const presets = useQuery({ queryKey: ['presets'], queryFn: () => api<PresetDto[]>('/api/difficulty-presets') })
  const [saving, setSaving] = useState(false)
  const [name, setName] = useState('')
  const savePreset = useMutation({
    mutationFn: () => api('/api/difficulty-presets', { json: { name, values: s.settings.difficulty } }),
    onSuccess: async () => { setSaving(false); setName(''); await queryClient.invalidateQueries({ queryKey: ['presets'] }) },
  })
  const overridden = s.difficultyOverride.trim() !== ''
  const loaded = presets.data?.find(x => x.id === s.difficultyPresetId)
  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <Panel title="Difficulty" meta={<Link to="/profiles/presets">Presets</Link>}>
        <div className="flex flex-col gap-5 px-6 py-5">
          <p className="m-0 text-sm text-text-muted">
            These values are the server's Custom difficulty. Missions use them with difficulty Custom, or always when Forced difficulty is Custom.
            {loaded && ` Last loaded from ${loaded.name}.`}
          </p>
          {overridden && <p role="status" className="m-0 border border-line bg-black/20 px-4 py-3 text-sm">Set in the raw editor: the class has entries this form doesn't know.</p>}
          {!readOnly && !overridden && (
            <div className="flex flex-wrap items-end gap-2">
              <SelectField label="Load a preset" value="" className="min-w-0 flex-1"
                onChange={e => {
                  const preset = presets.data?.find(x => x.id === e.target.value)
                  if (preset) draft.update(x => { x.settings.difficulty = { ...preset.values }; x.difficultyPresetId = preset.id })
                }}>
                <option value="">Choose a preset to copy in</option>
                {presets.data?.map(x => <option key={x.id} value={x.id}>{x.name}{x.isBuiltIn ? ' (Bohemia)' : ''}</option>)}
              </SelectField>
              <Button onClick={() => setSaving(true)}>Save as preset</Button>
            </div>
          )}
          <SettingsForm file="difficulty" tab="difficulty" values={s.settings.difficulty} errors={draft.fieldErrors} readOnly={readOnly || overridden}
            onChange={(k, v) => draft.setValue('difficulty', k, v)} />
        </div>
      </Panel>
      <Panel title="Profile file">
        <div className="px-6 py-5">
          <ConfigPreview title="DifficultyPresets class" file="difficulty" text={draft.preview?.files.difficultyPresets} draft={draft} />
        </div>
      </Panel>
      <Dialog open={saving} onClose={() => setSaving(false)} title="Save as preset">
        <form className="flex flex-col gap-4" onSubmit={e => { e.preventDefault(); savePreset.mutate() }}>
          <Field label="Preset name" value={name} maxLength={40} required onChange={e => setName(e.target.value)}
            error={savePreset.error ? errorMessage(savePreset.error) : undefined} hint="Profiles copy a preset's values, so editing it later never changes a server." />
          <div className="flex justify-end gap-2.5">
            <Button onClick={() => setSaving(false)}>Cancel</Button>
            <Button type="submit" variant="primary" busy={savePreset.isPending}>Save preset</Button>
          </div>
        </form>
      </Dialog>
    </div>
  )
}

export function StartupTab() {
  const { draft } = useProfile()
  const can = useCan()
  const readOnly = !can('profiles.manage')
  const s = draft.snapshot!
  const command = draft.preview?.command
  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <Panel title="Startup parameters">
        <div className="flex flex-col gap-5 px-6 py-5">
          <SettingsForm file="launch" tab="startup" values={s.settings.launch} errors={draft.fieldErrors} readOnly={readOnly}
            onChange={(k, v) => draft.setValue('launch', k, v)} />
          <Field label="Extra arguments" value={s.settings.extraArgs} maxLength={500} disabled={readOnly} error={draft.fieldErrors['launch.extraArgs']}
            className="[&_input]:font-mono"
            hint="Only the arguments listed above are allowed, each at most once. The agent sets -port, -config, -cfg, -profiles and -name itself."
            onChange={e => draft.update(x => { x.settings.extraArgs = e.target.value })} />
        </div>
      </Panel>
      <Panel title="Command line">
        <div className="flex flex-col gap-3 px-6 py-5">
          {command ? (
            <>
              <div className="relative overflow-x-auto border border-line bg-console">
                <pre className="m-0 p-3 font-mono text-sm whitespace-pre-wrap break-all text-text-secondary">{[command.server, ...command.headless].join('\n\n')}</pre>
              </div>
              <CopyField value={command.server} label="command line" />
            </>
          ) : <p className="m-0 text-text-muted">{draft.previewError ? errorMessage(draft.previewError) : 'Building the command line…'}</p>}
        </div>
      </Panel>
    </div>
  )
}
