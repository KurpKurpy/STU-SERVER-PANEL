import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Button } from '../../components/Button'
import { Field, SelectField, TextArea } from '../../components/Field'
import { formatWhen } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { SecretField } from '../../components/SecretField'
import { ApiError, api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { InstallDto, SecretsWrite } from '../../lib/types'
import { ConfigPreview } from './ConfigPreview'
import { appliedState, useProfile } from './ProfileLayout'
import { ServerPresets } from './ServerPresets'
import { SettingsForm } from './SettingsForm'

export const executables = ['arma3server_x64.exe', 'arma3serverprofiling_x64.exe']

// Managed classes whose text in the extras holds entries the form doesn't know (plan Ruling 5): their fields are read-only.
export function rawClasses(extra: string, names: string[]) {
  return names.filter(c => new RegExp(`^\\s*class\\s+${c}\\b`, 'mi').test(extra))
}

export function ProfileOverview() {
  const { draft } = useProfile()
  const p = draft.detail.data!
  const state = appliedState(p)
  return (
    <div className="flex flex-col gap-4">
    <Panel title="Overview">
      <div className="flex flex-col gap-4 px-6 py-5">
        {p.needsCfg && (
          <p role="alert" className="m-0 border border-accent/45 bg-tint px-4 py-3 text-sm">
            This profile's server.cfg couldn't be carried over from Phase 2. Paste it on the <Link to="server-config">Server config</Link> tab with Edit raw, or import it there.
          </p>
        )}
        {p.secretsUnreadable && (
          <p role="alert" className="m-0 border border-accent/45 bg-tint px-4 py-3 text-sm">
            The saved passwords can't be decrypted (the panel's encryption keys changed). Replace or clear each of the three on the <Link to="general">General</Link> tab, then save; saving is refused until then.
          </p>
        )}
        <dl className="m-0 grid grid-cols-1 gap-x-8 gap-y-3 sm:grid-cols-2">
          {[
            ['Install', `${p.installName} · ${p.executable}`],
            ['Ports', `${p.port}–${p.port + 4}`],
            ['Public address', p.publicAddress ?? '—'],
            ['Latest revision', `r${p.revision}${p.updatedAt ? ` · ${formatWhen(p.updatedAt)}` : ''}`],
            ['On the server', p.appliedRevision === null ? 'Never applied' : `r${p.appliedRevision} · ${formatWhen(p.appliedAt)}`],
            ['State', state.label],
          ].map(([k, v]) => (
            <div key={k} className="flex flex-col gap-0.5">
              <dt className="text-2xs font-semibold uppercase tracking-label text-text-muted">{k}</dt>
              <dd className="m-0 break-words">{v}</dd>
            </div>
          ))}
        </dl>
      </div>
    </Panel>
    <ServerPresets />
    </div>
  )
}

export function GeneralTab() {
  const { draft, running } = useProfile()
  const can = useCan()
  const p = draft.detail.data!
  const s = draft.snapshot!
  const readOnly = !can('profiles.manage')
  const setSecret = (k: keyof SecretsWrite) => (v: string | undefined) =>
    draft.setSecrets(prev => { const next = { ...prev }; if (v === undefined) delete next[k]; else next[k] = v; return next })
  return (
    <div className="flex flex-col gap-4">
      <IdentityPanel running={running} readOnly={readOnly} />
      <Panel title="Server">
        <div className="flex flex-col gap-6 px-6 py-5">
          <SettingsForm file="server" tab="general" values={s.settings.server} errors={draft.fieldErrors} readOnly={readOnly}
            onChange={(k, v) => draft.setValue('server', k, v)} />
          <fieldset className="m-0 flex flex-col gap-4 border-0 p-0">
            <legend className="mb-3 text-sm font-semibold text-text-secondary">Passwords</legend>
            <p className="m-0 text-xs text-text-muted">Stored encrypted and never shown again. Members with "See the server's join password" see the applied join password.</p>
            <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
              <SecretField label="Join password" set={p.secrets.password.set} unreadable={p.secretsUnreadable} value={draft.secrets.password} onChange={setSecret('password')} disabled={readOnly} />
              <SecretField label="Admin password" set={p.secrets.passwordAdmin.set} unreadable={p.secretsUnreadable} value={draft.secrets.passwordAdmin} onChange={setSecret('passwordAdmin')} disabled={readOnly} />
              <SecretField label="Server command password" set={p.secrets.serverCommandPassword.set} unreadable={p.secretsUnreadable} value={draft.secrets.serverCommandPassword}
                onChange={setSecret('serverCommandPassword')} disabled={readOnly} />
            </div>
            {!readOnly && <RconPassword profileId={p.id} />}
          </fieldset>
        </div>
      </Panel>
    </div>
  )
}

// Phase 7a Ruling 4: the console makes and keeps the BattlEye RCon password itself; this only replaces it, from the next start.
function RconPassword({ profileId }: { profileId: string }) {
  const renew = useMutation({ mutationFn: () => api(`/api/profiles/${profileId}/rcon-password`, { method: 'POST' }) })
  return (
    <div className="flex flex-wrap items-center gap-3 text-sm">
      <span className="text-text-secondary">BattlEye RCon password: made and kept by the console, never shown; RCon only listens on this box.</span>
      <Button size="sm" busy={renew.isPending} onClick={() => renew.mutate()}>New RCon password</Button>
      {renew.isSuccess && <span role="status" className="text-text-secondary">Done. It's used from the next start.</span>}
      {renew.error && <span role="alert" className="text-accent-text">{errorMessage(renew.error)}</span>}
    </div>
  )
}

// Name and address any time; install, executable and port only while the server is stopped. Not revisioned (plan Ruling 3).
function IdentityPanel({ running, readOnly }: { running: boolean; readOnly: boolean }) {
  const { draft } = useProfile()
  const p = draft.detail.data!
  const queryClient = useQueryClient()
  const installs = useQuery({ queryKey: ['installs'], queryFn: () => api<InstallDto[]>('/api/installs'), enabled: !readOnly })
  const [v, setV] = useState({ name: p.name, publicAddress: p.publicAddress ?? '', serverInstallId: p.serverInstallId, executable: p.executable, port: String(p.port) })
  const set = (k: keyof typeof v) => (value: string) => setV(prev => ({ ...prev, [k]: value }))
  const save = useMutation({
    mutationFn: () => api(`/api/profiles/${p.id}`, {
      method: 'PATCH', json: { name: v.name, publicAddress: v.publicAddress, serverInstallId: v.serverInstallId, executable: v.executable, port: Number(v.port) },
    }),
    onSuccess: () => Promise.all(['profile', 'profiles', 'servers'].map(k => queryClient.invalidateQueries({ queryKey: [k] }))),
  })
  const err = (f: string) => (save.error instanceof ApiError && save.error.field === f ? save.error.message : undefined)
  const locked = running ? 'Stop the server to change this.' : undefined
  function submit(e: FormEvent) { e.preventDefault(); save.mutate() }
  return (
    <Panel title="Identity" meta={<span className="font-mono">{p.slug}</span>}>
      <form onSubmit={submit} className="flex flex-col gap-4 px-6 py-5">
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <Field label="Name" value={v.name} maxLength={40} required disabled={readOnly} error={err('name')} onChange={e => set('name')(e.target.value)} />
          <Field label="Public address" value={v.publicAddress} maxLength={253} disabled={readOnly} error={err('publicAddress')}
            hint="What members type to join, e.g. play.example.org." onChange={e => set('publicAddress')(e.target.value)} />
          <SelectField label="Server install" value={v.serverInstallId} disabled={readOnly || running} hint={locked} onChange={e => set('serverInstallId')(e.target.value)}>
            {(installs.data ?? [{ id: p.serverInstallId, name: p.installName, installPath: '' } as InstallDto]).map(i =>
              <option key={i.id} value={i.id}>{i.name}{i.installPath ? ` (${i.installPath})` : ''}</option>)}
          </SelectField>
          <SelectField label="Executable" value={v.executable} disabled={readOnly || running} hint={locked} onChange={e => set('executable')(e.target.value)}>
            {executables.map(x => <option key={x} value={x}>{x}</option>)}
          </SelectField>
          <Field label="Game port" type="number" min={1024} max={65530} value={v.port} required disabled={readOnly || running} error={err('port')}
            hint={locked ?? `Uses ports ${v.port}–${Number(v.port) + 4}. The slug (${p.slug}) names its folders and can't change.`}
            onChange={e => set('port')(e.target.value)} />
        </div>
        {save.error && !(save.error instanceof ApiError && save.error.field) && <p role="alert" className="m-0 text-accent-text">{errorMessage(save.error)}</p>}
        {!readOnly && <div className="flex justify-end"><Button type="submit" variant="primary" busy={save.isPending}>Save identity</Button></div>}
      </form>
    </Panel>
  )
}

export function ServerConfigTab() {
  const { draft } = useProfile()
  const can = useCan()
  const s = draft.snapshot!
  const readOnly = !can('profiles.manage')
  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <Panel title="Server config">
        <div className="flex flex-col gap-6 px-6 py-5">
          <SettingsForm file="server" tab="server" values={s.settings.server} errors={draft.fieldErrors} readOnly={readOnly}
            rawClasses={rawClasses(s.extraServerCfg, ['AntiFlood', 'AdvancedOptions'])} onChange={(k, v) => draft.setValue('server', k, v)} />
          <TextArea label="Other server.cfg entries" rows={6} spellCheck={false} disabled={readOnly} value={s.extraServerCfg}
            error={draft.fieldErrors.extraServerCfg} className="[&_textarea]:font-mono [&_textarea]:text-sm"
            hint="Kept exactly as written, below the managed settings. For settings without a field, such as kickTimeout[] or disableChannels[]."
            onChange={e => draft.update(x => { x.extraServerCfg = e.target.value })} />
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

export function PerformanceTab() {
  const { draft } = useProfile()
  const can = useCan()
  const s = draft.snapshot!
  const readOnly = !can('profiles.manage')
  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <Panel title="Performance">
        <div className="flex flex-col gap-6 px-6 py-5">
          <SettingsForm file="basic" tab="performance" values={s.settings.basic} errors={draft.fieldErrors} readOnly={readOnly}
            rawClasses={rawClasses(s.extraBasicCfg, ['sockets'])} onChange={(k, v) => draft.setValue('basic', k, v)} />
          <TextArea label="Other basic.cfg entries" rows={4} spellCheck={false} disabled={readOnly} value={s.extraBasicCfg}
            error={draft.fieldErrors.extraBasicCfg} className="[&_textarea]:font-mono [&_textarea]:text-sm"
            onChange={e => draft.update(x => { x.extraBasicCfg = e.target.value })} />
        </div>
      </Panel>
      <Panel title="basic.cfg">
        <div className="px-6 py-5">
          <ConfigPreview title="Generated basic.cfg" file="basic.cfg" text={draft.preview?.files.basicCfg} draft={draft}
            empty="No basic.cfg is written while nothing is set." />
        </div>
      </Panel>
    </div>
  )
}
