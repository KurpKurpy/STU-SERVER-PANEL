import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button, buttonClasses } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, formatWhen, Loading } from '../../components/Feedback'
import { Check, Field, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock, type Tone } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { formatBytes } from '../../lib/format'
import type { BackupDto, BackupSettings } from '../../lib/types'

const kinds: Record<BackupDto['kind'], string> = { Manual: 'By hand', Scheduled: 'Scheduled', PreUpdate: 'Before an update', PreRestore: 'Before a restore' }
const tones: Record<BackupDto['status'], Tone> = { Running: 'warn', Done: 'ok', Failed: 'bad' }
const parts = [
  { key: 'profiles', label: 'Server profiles', hint: 'logs, persistence, BattlEye bans' },
  { key: 'configs', label: 'Generated configs', hint: 'server.cfg, basic.cfg (Apply writes these again anyway)' },
  { key: 'missions', label: 'Missions folders', hint: 'each install’s mpmissions' },
  { key: 'keys', label: 'Mod keys', hint: 'each install’s keys folder' },
]
const label = (b: BackupDto) => b.type === 'Console' ? 'Console' : `Server${b.agentName ? ` (${b.agentName})` : ''}`

// Phase 7b: console backups (database, uploaded missions, keys) and server backups (profiles, configs, missions, keys), with retention.
export function BackupsPage() {
  const queryClient = useQueryClient()
  const list = useQuery({
    queryKey: ['backups'], queryFn: () => api<BackupDto[]>('/api/backups'),
    refetchInterval: q => (q.state.data?.some(b => b.status === 'Running') ? 3000 : false),
  })
  const settings = useQuery({ queryKey: ['backups', 'settings'], queryFn: () => api<BackupSettings>('/api/backups/settings') })
  const [agentId, setAgentId] = useState('')
  const [restoring, setRestoring] = useState<BackupDto | null>(null)
  const [deleting, setDeleting] = useState<BackupDto | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['backups'] })
  const start = useMutation({ mutationFn: (body: { type: string; agentId?: string }) => api('/api/backups', { json: body }), onSuccess: refresh })
  const agents = settings.data?.agents ?? []
  const agent = agentId || agents[0]?.id || ''

  return (
    <div className="stagger grid grid-cols-1 items-start gap-6 md:grid-cols-12">
      <Panel title="Backups" className="md:col-span-12" meta={
        <div className="flex flex-wrap items-end gap-2">
          <Button size="sm" busy={start.isPending && start.variables?.type === 'console'} onClick={() => start.mutate({ type: 'console' })}>Back up the console</Button>
          {agents.length > 1 && (
            <SelectField label="Server agent" className="w-40" value={agent} onChange={e => setAgentId(e.target.value)}>
              {agents.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
            </SelectField>
          )}
          {agents.length > 0 && <Button size="sm" busy={start.isPending && start.variables?.type === 'server'} onClick={() => start.mutate({ type: 'server', agentId: agent })}>Back up the server</Button>}
        </div>
      }>
        {start.error && <p role="alert" className="m-0 border-b border-line px-6 py-3 text-sm text-accent-text">{errorMessage(start.error)}</p>}
        {list.isPending && <div className="px-6 py-5"><Loading /></div>}
        {list.error && <div className="px-6 py-5"><ErrorNote error={list.error} onRetry={() => list.refetch()} /></div>}
        {list.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No backups yet. Make one now, or set up a backup schedule.</p>}
        {list.data && list.data.length > 0 && (
          <ul className="m-0 list-none p-0">
            {list.data.map(b => (
              <li key={b.id} className="flex flex-wrap items-center gap-x-4 gap-y-1 border-t border-line px-6 py-3 text-sm first:border-t-0">
                <span className="min-w-28 font-mono tabular-nums">{formatWhen(b.createdAt)}</span>
                <span className="min-w-0 flex-1 basis-56">
                  <span className="font-semibold">{label(b)}</span>
                  <span className="text-text-secondary"> · {kinds[b.kind]}{b.createdBy ? ` · ${b.createdBy}` : ''}{b.status === 'Done' ? ` · ${formatBytes(b.sizeBytes)}` : ''}</span>
                  {(b.error || b.note) && <span className={`block text-xs ${b.error ? 'text-accent-text' : 'text-text-muted'}`}>{b.error ?? b.note}</span>}
                </span>
                <StatusBlock tone={tones[b.status]}>{b.status === 'Running' ? 'Making' : b.status}</StatusBlock>
                <span className="flex flex-wrap gap-2">
                  {b.status === 'Done' && <a className={buttonClasses('default', 'sm')} href={`/api/backups/${b.id}/download`} download>Download</a>}
                  {b.status === 'Done' && <Button size="sm" onClick={() => setRestoring(b)}>Restore</Button>}
                  {b.status !== 'Running' && <Button size="sm" variant="danger" onClick={() => setDeleting(b)}>Delete</Button>}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      {settings.data && <SettingsPanel settings={settings.data} />}

      <Dialog open={!!restoring} onClose={() => setRestoring(null)} title={restoring?.type === 'Console' ? 'Restore the console' : 'Restore server files'}>
        {restoring && <RestoreForm backup={restoring} onDone={() => { setRestoring(null); void refresh() }} />}
      </Dialog>
      <Dialog open={!!deleting} onClose={() => setDeleting(null)} title="Delete this backup?">
        {deleting && <DeleteForm backup={deleting} onDone={() => { setDeleting(null); void refresh() }} />}
      </Dialog>
    </div>
  )
}

function RestoreForm({ backup, onDone }: { backup: BackupDto; onDone: () => void }) {
  const [chosen, setChosen] = useState<string[]>(['profiles'])
  const restore = useMutation({ mutationFn: () => api(`/api/backups/${backup.id}/restore`, { json: { parts: chosen } }) })
  if (backup.type === 'Console') return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-text-secondary">A console backup is restored on the box itself, because the running console can’t replace its own database:</p>
      <ol className="m-0 flex flex-col gap-1 pl-5 text-text-secondary">
        <li>Download <span className="font-mono text-text">{backup.fileName}</span> to the box.</li>
        <li>Stop the <strong className="text-text">13th Console</strong> service.</li>
        <li>Run <span className="font-mono text-text">Panel.exe restore &lt;path to the zip&gt;</span> in the console’s folder.</li>
        <li>Start the service again. The old data is kept in a <span className="font-mono">before-restore-…</span> folder.</li>
      </ol>
      <div className="flex justify-end"><Button onClick={onDone}>Close</Button></div>
    </div>
  )
  if (restore.isSuccess) return (
    <div className="flex flex-col gap-4">
      <p role="status" className="m-0 text-text-secondary">The restore is running on {backup.agentName ?? 'the agent'}. It first saves the current files as a “Before a restore” backup in the list. If the restore fails, the reason shows in red on this backup’s line.</p>
      <div className="flex justify-end"><Button onClick={onDone}>Close</Button></div>
    </div>
  )
  const toggle = (key: string, on: boolean) => setChosen(c => on ? [...c, key] : c.filter(k => k !== key))
  return (
    <form className="flex flex-col gap-4" onSubmit={(e: FormEvent) => { e.preventDefault(); restore.mutate() }}>
      <p className="m-0 text-text-secondary">
        Writes the chosen parts of <span className="font-mono text-text">{backup.fileName}</span> back onto {backup.agentName ?? 'the agent'}.
        <strong className="text-text"> Every server on it must be stopped.</strong> The current files are backed up first; files added since this backup are kept.
      </p>
      <fieldset className="m-0 flex flex-col gap-1 border-0 p-0">
        <legend className="mb-1 font-mono text-2xs uppercase tracking-label text-text-muted">Restore</legend>
        {parts.map(p => <Check key={p.key} label={p.label} hint={p.hint} checked={chosen.includes(p.key)} onChange={e => toggle(p.key, e.target.checked)} />)}
      </fieldset>
      {restore.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(restore.error)}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant="danger" busy={restore.isPending} disabled={chosen.length === 0}>Restore</Button>
      </div>
    </form>
  )
}

function DeleteForm({ backup, onDone }: { backup: BackupDto; onDone: () => void }) {
  const del = useMutation({ mutationFn: () => api(`/api/backups/${backup.id}`, { method: 'DELETE' }), onSuccess: onDone })
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-text-secondary">
        Deletes <span className="font-mono text-text">{backup.fileName ?? 'this failed backup'}</span>{backup.fileName ? ' and its copy elsewhere' : ''}. This can’t be undone.
      </p>
      {del.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(del.error)}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="danger" busy={del.isPending} onClick={() => del.mutate()}>Delete</Button>
      </div>
    </div>
  )
}

function SettingsPanel({ settings }: { settings: BackupSettings }) {
  const queryClient = useQueryClient()
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const save = useMutation({
    mutationFn: (body: { keepScheduled: number; keepPreUpdate: number; safetyCopyDays: number }) => api('/api/backups/settings', { method: 'PUT', json: body }),
    onSuccess: async () => { await queryClient.invalidateQueries({ queryKey: ['backups', 'settings'] }); setMessage({ ok: true, text: 'Saved.' }) },
    onError: err => setMessage({ ok: false, text: errorMessage(err) }),
  })
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    save.mutate({ keepScheduled: Number(f.get('keepScheduled')), keepPreUpdate: Number(f.get('keepPreUpdate')), safetyCopyDays: Number(f.get('safetyCopyDays')) })
  }
  return (
    <Panel title="Keeping backups" className="md:col-span-12">
      <form onSubmit={submit} className="flex flex-col gap-5 px-6 pt-5 pb-6">
        <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <Field label="Scheduled backups to keep" name="keepScheduled" type="number" min={1} max={365} defaultValue={settings.keepScheduled} hint="Per type; the oldest go first." />
          <Field label="Before-update backups to keep" name="keepPreUpdate" type="number" min={1} max={100} defaultValue={settings.keepPreUpdate} />
          <Field label="Delete safety copies after (days)" name="safetyCopyDays" type="number" min={0} max={3650} defaultValue={settings.safetyCopyDays} hint="The automatic copies made before changes. 0 keeps them forever." />
        </div>
        <p className="m-0 max-w-prose text-sm text-text-secondary">
          Backups you make by hand, and those taken before a restore, are only deleted by you.
          {' '}Copies elsewhere: the console’s go to <span className="font-mono text-text">{settings.consoleCopyTo ?? 'nowhere yet'}</span>.
          Set <span className="font-mono">Backups:CopyTo</span> in the console’s appsettings.json and <span className="font-mono">Agent:BackupCopyTo</span> in each agent’s, on the box.
        </p>
        {message && <p role={message.ok ? 'status' : 'alert'} className={`m-0 ${message.ok ? 'text-ok' : 'text-accent-text'}`}>{message.text}</p>}
        <Button type="submit" variant="primary" busy={save.isPending} className="self-start">Save</Button>
      </form>
    </Panel>
  )
}
