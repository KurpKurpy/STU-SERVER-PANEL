import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChecklistSettings } from '../operations/ChecklistSettings'
import { NotificationSettings } from '../notifications/NotificationSettings'
import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { Button, buttonClasses } from '../../components/Button'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Check, Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan, useMe } from '../../lib/auth'
import type { RoleDto, SettingsDto } from '../../lib/types'

export function SettingsPage() {
  const can = useCan()
  return (
    <div className="stagger grid grid-cols-1 items-start gap-5 md:grid-cols-12">
      <AccountPanel />
      {can('settings.manage') && <UnitSettingsPanel />}
      {can('settings.manage') && <ChecklistSettings className="md:col-span-12" />}
      {can('settings.manage') && <NotificationSettings className="md:col-span-12" />}
    </div>
  )
}

function AccountPanel() {
  const { data: me } = useMe()
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const change = useMutation({
    mutationFn: (body: { current: string; new: string }) => api('/api/account/password', { json: body }),
    onSuccess: () => setMessage({ ok: true, text: 'Password changed.' }),
    onError: err => setMessage({ ok: false, text: errorMessage(err) }),
  })

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const data = new FormData(form)
    if (data.get('new') !== data.get('confirm')) { setMessage({ ok: false, text: "The new passwords don't match." }); return }
    change.mutate({ current: String(data.get('current')), new: String(data.get('new')) }, { onSuccess: () => form.reset() })
  }

  return (
    <Panel title="My account" className="md:col-span-5" meta={me?.userName}>
      <div className="flex flex-col gap-5 px-6 pt-5 pb-6">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <span className="flex items-center gap-2.5">Two-factor sign-in
            {me?.twoFactorEnabled ? <StatusBlock tone="ok">On</StatusBlock> : <StatusBlock tone="warn">Off</StatusBlock>}
          </span>
          {!me?.twoFactorEnabled && <Link to="/account/2fa" className={buttonClasses('default', 'sm')}>Set up</Link>}
        </div>
        <form onSubmit={submit} className="flex flex-col gap-4 border-t border-line pt-5">
          <Field label="Current password" name="current" type="password" autoComplete="current-password" required />
          <Field label="New password" name="new" type="password" autoComplete="new-password" minLength={12} required hint="At least 12 characters." />
          <Field label="Confirm new password" name="confirm" type="password" autoComplete="new-password" required />
          {message && <p role={message.ok ? 'status' : 'alert'} className={`m-0 ${message.ok ? 'text-ok' : 'text-accent-text'}`}>{message.text}</p>}
          <Button type="submit" busy={change.isPending} className="self-start">Change password</Button>
        </form>
      </div>
    </Panel>
  )
}

function UnitSettingsPanel() {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: ['settings'], queryFn: () => api<SettingsDto>('/api/settings') })
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api<RoleDto[]>('/api/roles') })
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const save = useMutation({
    mutationFn: (body: SettingsDto) => api<SettingsDto>('/api/settings', { method: 'PUT', json: body }),
    onSuccess: async () => {
      await Promise.all(['settings', 'me'].map(k => queryClient.invalidateQueries({ queryKey: [k] })))
      setMessage({ ok: true, text: 'Saved.' })
    },
    onError: err => setMessage({ ok: false, text: errorMessage(err) }),
  })

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    save.mutate({ appName: String(form.get('appName') ?? ''), twoFactorRequiredRoles: form.getAll('twoFactorRole').map(String),
      unitTimeZone: String(form.get('unitTimeZone') ?? '').trim() || undefined })
  }

  const loading = settings.isPending || roles.isPending
  return (
    <Panel title="Unit settings" className="md:col-span-7">
      <div className="px-6 pt-5 pb-6">
        {loading && <Loading />}
        {(settings.error || roles.error) && <ErrorNote error={settings.error ?? roles.error} />}
        {settings.data && roles.data && (
          <form onSubmit={submit} className="flex flex-col gap-5">
            <Field label="App name" name="appName" defaultValue={settings.data.appName} maxLength={40} required
              hint="Shown in the sidebar, browser tabs and the authenticator app." />
            <Field label="Unit time zone" name="unitTimeZone" defaultValue={settings.data.unitTimeZone ?? 'Australia/Sydney'} maxLength={60} list="time-zones"
              hint="Schedules run in this zone, e.g. Australia/Sydney (follows daylight saving) or Australia/Brisbane (doesn't)." />
            <datalist id="time-zones">{['Australia/Sydney', 'Australia/Melbourne', 'Australia/Brisbane', 'Australia/Adelaide', 'Australia/Perth', 'Pacific/Auckland', 'UTC'].map(z => <option key={z} value={z} />)}</datalist>
            <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
              <legend className="mb-1 font-mono text-2xs uppercase tracking-label text-text-muted">Require two-factor sign-in for</legend>
              {roles.data.map(r => (
                <Check key={r.id} name="twoFactorRole" value={r.name} label={r.name}
                  defaultChecked={settings.data.twoFactorRequiredRoles.includes(r.name)}
                  hint={`${r.memberCount} member${r.memberCount === 1 ? '' : 's'}`} />
              ))}
            </fieldset>
            {message && <p role={message.ok ? 'status' : 'alert'} className={`m-0 ${message.ok ? 'text-ok' : 'text-accent-text'}`}>{message.text}</p>}
            <Button type="submit" variant="primary" busy={save.isPending} className="self-start">Save settings</Button>
          </form>
        )}
      </div>
    </Panel>
  )
}
