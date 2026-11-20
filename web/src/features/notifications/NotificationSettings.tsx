import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Button } from '../../components/Button'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { SecretField } from '../../components/SecretField'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import type { NotificationRuleDto, NotificationSettingsDto } from '../../lib/types'

// Phase 6b Ruling 13: which events go in-app and to Discord, the reminder lead time, and the write-only Discord webhook.
export function NotificationSettings({ className = '' }: { className?: string }) {
  const queryClient = useQueryClient()
  const settings = useQuery({ queryKey: ['notification-settings'], queryFn: () => api<NotificationSettingsDto>('/api/notifications/settings') })
  const [rules, setRules] = useState<NotificationRuleDto[]>([])
  const [minutes, setMinutes] = useState('60')
  const [url, setUrl] = useState<string | undefined>(undefined)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState<'save' | 'test' | null>(null)
  useEffect(() => {
    if (!settings.data) return
    setRules(settings.data.rules)
    setMinutes(String(settings.data.reminderMinutes))
  }, [settings.data])

  const set = (type: string, change: Partial<NotificationRuleDto>) => setRules(rs => rs.map(r => (r.type === type ? { ...r, ...change } : r)))
  async function run(kind: 'save' | 'test', work: () => Promise<string>) {
    setBusy(kind)
    setMessage(null)
    try { setMessage({ ok: true, text: await work() }) } catch (e) { setMessage({ ok: false, text: errorMessage(e) }) } finally {
      setBusy(null)
      await queryClient.invalidateQueries({ queryKey: ['notification-settings'] })
    }
  }
  const save = () => run('save', async () => {
    if (url !== undefined) await api('/api/notifications/discord', { method: 'PUT', json: { url } })
    await api('/api/notifications/settings', { method: 'PUT', json: { reminderMinutes: Number(minutes), rules: rules.map(r => ({ type: r.type, inApp: r.inApp, discord: r.discord })) } })
    setUrl(undefined)
    return 'Notification settings saved.'
  })
  const test = () => run('test', async () => { await api('/api/notifications/discord/test', { method: 'POST' }); return 'Test message sent. Check the channel.' })

  const d = settings.data?.discord
  return (
    <Panel title="Notifications" className={className} meta={<Button size="sm" variant="primary" busy={busy === 'save'} onClick={save}>Save</Button>}>
      {settings.isPending && <div className="px-6 py-5"><Loading /></div>}
      {settings.error && <div className="px-6 py-5"><ErrorNote error={settings.error} onRetry={() => settings.refetch()} /></div>}
      {d && (
        <div className="grid gap-5 px-6 py-5 md:grid-cols-2">
          <div className="flex min-w-0 flex-col gap-2">
            <SecretField label="Discord webhook" set={d.set} value={url} onChange={setUrl} placeholder="https://discord.com/api/webhooks/…" />
            <p className="m-0 text-sm text-text-secondary">
              In Discord: Server Settings → Integrations → Webhooks → New Webhook → Copy Webhook URL. Optional; without it, notifications stay in the console.
            </p>
            {d.set && (
              <div className="flex flex-wrap items-center gap-2 text-sm">
                {d.broken
                  ? <StatusBlock tone="bad">Not working</StatusBlock>
                  : d.lastError ? <StatusBlock tone="warn">Last send failed</StatusBlock> : <StatusBlock tone="ok">Working</StatusBlock>}
                {d.lastError && <span className="text-text-secondary">{d.lastError}{d.lastErrorAt ? ` (${formatWhen(d.lastErrorAt)})` : ''}</span>}
                {!d.lastError && d.lastSentAt && <span className="text-text-secondary">Last sent {formatWhen(d.lastSentAt)}</span>}
                <Button size="sm" busy={busy === 'test'} disabled={url !== undefined} onClick={test}>Send a test message</Button>
              </div>
            )}
          </div>
          <Field label="Remind before an operation (minutes)" type="number" min={0} max={1440} value={minutes} onChange={e => setMinutes(e.target.value)}
            hint="0 turns reminders off." />
        </div>
      )}
      {rules.length > 0 && (
        <div className="relative overflow-x-auto border-t border-line">
          <table className="w-full border-collapse text-sm">
            <thead>
              <tr className="text-left text-2xs font-semibold uppercase tracking-label text-text-muted">
                <th scope="col" className="px-6 py-2">Event</th><th scope="col" className="px-3 py-2">In the console</th><th scope="col" className="px-3 py-2">Discord</th>
              </tr>
            </thead>
            <tbody>
              {rules.map(r => (
                <tr key={r.type} className="border-t border-line">
                  <th scope="row" className="px-6 py-2 text-left font-semibold">{r.label}</th>
                  <td className="px-3 py-2"><input type="checkbox" aria-label={`${r.label}: in the console`} checked={r.inApp} onChange={e => set(r.type, { inApp: e.target.checked })} /></td>
                  <td className="px-3 py-2"><input type="checkbox" aria-label={`${r.label}: Discord`} checked={r.discord} onChange={e => set(r.type, { discord: e.target.checked })} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {message && <p role={message.ok ? 'status' : 'alert'} className={`m-0 border-t border-line px-6 py-3 text-sm ${message.ok ? 'text-text-secondary' : 'text-accent-text'}`}>{message.text}</p>}
    </Panel>
  )
}
