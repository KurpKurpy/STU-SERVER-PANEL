import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, formatWhen, Loading } from '../../components/Feedback'
import { Field, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock, type Tone } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import type { ScheduleDto, ScheduleRunDto, ScheduleTargets, ScheduleTask } from '../../lib/types'

export const taskLabels: Record<ScheduleTask, string> = {
  restart: 'Restart a server', stop: 'Stop a server', start: 'Start a server',
  'update-server': 'Update the server (SteamCMD)', 'update-mods': 'Update Workshop mods', backup: 'Back up',
}
const outcomeTones: Record<ScheduleRunDto['outcome'], Tone> = { Running: 'warn', Done: 'ok', Failed: 'bad', Skipped: 'warn', Missed: 'bad' }
const weekdays = [{ bit: 2, label: 'Mon' }, { bit: 4, label: 'Tue' }, { bit: 8, label: 'Wed' }, { bit: 16, label: 'Thu' }, { bit: 32, label: 'Fri' }, { bit: 64, label: 'Sat' }, { bit: 1, label: 'Sun' }]
const hhmm = (minute: number) => `${String(Math.floor(minute / 60)).padStart(2, '0')}:${String(minute % 60).padStart(2, '0')}`
const dayText = (days: number) => days === 127 ? 'Every day' : days === 62 ? 'Weekdays' : days === 65 ? 'Weekends' : weekdays.filter(d => days & d.bit).map(d => d.label).join(', ')

// Phase 7b: restarts, updates and backups on chosen days at a time in the unit's time zone; never near an operation.
export function SchedulesPage() {
  const queryClient = useQueryClient()
  const list = useQuery({
    queryKey: ['schedules'], queryFn: () => api<ScheduleDto[]>('/api/schedules'),
    refetchInterval: q => (q.state.data?.some(s => s.lastRun?.outcome === 'Running') ? 3000 : 30_000),
  })
  const targets = useQuery({ queryKey: ['schedules', 'targets'], queryFn: () => api<ScheduleTargets>('/api/schedules/targets') })
  const [editing, setEditing] = useState<ScheduleDto | 'new' | null>(null)
  const [deleting, setDeleting] = useState<ScheduleDto | null>(null)
  const [history, setHistory] = useState<ScheduleDto | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['schedules'] })
  const runNow = useMutation({ mutationFn: (id: string) => api(`/api/schedules/${id}/run`, { method: 'POST' }), onSettled: refresh })
  const toggle = useMutation({
    mutationFn: (s: ScheduleDto) => api(`/api/schedules/${s.id}`, { method: 'PUT', json: { ...bodyOf(s), enabled: !s.enabled } }), onSettled: refresh,
  })

  return (
    <div className="stagger grid grid-cols-1 items-start gap-6 md:grid-cols-12">
      <Panel title="Schedules" className="md:col-span-12" meta={<Button size="sm" variant="primary" onClick={() => setEditing('new')}>New schedule</Button>}>
        <p className="m-0 border-b border-line px-6 py-3 text-sm text-text-secondary">
          Times are in {targets.data?.timeZone ?? 'the unit’s time zone'} (Settings). Restarts, stops and updates never run from 2 hours before an operation
          until 4 hours after it starts; backups still do. Players get warnings in game at 10, 5 and 1 minutes, and updates wait until nobody’s on.
        </p>
        {(runNow.error || toggle.error) && <p role="alert" className="m-0 border-b border-line px-6 py-3 text-sm text-accent-text">{errorMessage(runNow.error ?? toggle.error)}</p>}
        {list.isPending && <div className="px-6 py-5"><Loading /></div>}
        {list.error && <div className="px-6 py-5"><ErrorNote error={list.error} onRetry={() => list.refetch()} /></div>}
        {list.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No schedules yet. A nightly backup is a good first one.</p>}
        {list.data && list.data.length > 0 && (
          <ul className="m-0 list-none p-0">
            {list.data.map(s => (
              <li key={s.id} className="flex flex-wrap items-center gap-x-4 gap-y-2 border-t border-line px-6 py-3 text-sm first:border-t-0">
                <span className="min-w-0 flex-1 basis-56">
                  <span className="font-semibold">{s.name}</span>
                  <span className="block text-text-secondary">{taskLabels[s.task]}{s.targetName ? `: ${s.targetName}` : s.scope ? `: ${s.scope}` : ''} · {dayText(s.days)} at {hhmm(s.minuteOfDay)}</span>
                  <span className="block text-xs text-text-muted">
                    {s.enabled ? `Next: ${formatWhen(s.nextRunAt)}` : 'Off'}
                    {s.lastRun && <> · Last: {s.lastRun.outcome.toLowerCase()}{s.lastRun.detail ? ` (${s.lastRun.detail})` : ''}</>}
                  </span>
                </span>
                {s.lastRun && <StatusBlock tone={outcomeTones[s.lastRun.outcome]}>{s.lastRun.outcome}</StatusBlock>}
                <span className="flex flex-wrap gap-2">
                  <Button size="sm" busy={toggle.isPending && toggle.variables?.id === s.id} onClick={() => toggle.mutate(s)}>{s.enabled ? 'Turn off' : 'Turn on'}</Button>
                  <Button size="sm" busy={runNow.isPending && runNow.variables === s.id} disabled={s.lastRun?.outcome === 'Running'} onClick={() => runNow.mutate(s.id)}>Run now</Button>
                  <Button size="sm" onClick={() => setHistory(s)}>History</Button>
                  <Button size="sm" onClick={() => setEditing(s)}>Edit</Button>
                  <Button size="sm" variant="danger" onClick={() => setDeleting(s)}>Delete</Button>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Dialog open={!!editing} onClose={() => setEditing(null)} title={editing === 'new' ? 'New schedule' : `Edit ${editing?.name ?? ''}`}>
        {editing && targets.data && <ScheduleForm schedule={editing === 'new' ? null : editing} targets={targets.data} onDone={() => { setEditing(null); void refresh() }} />}
      </Dialog>
      <Dialog open={!!deleting} onClose={() => setDeleting(null)} title="Delete this schedule?">
        {deleting && <DeleteSchedule schedule={deleting} onDone={() => { setDeleting(null); void refresh() }} />}
      </Dialog>
      <Dialog open={!!history} onClose={() => setHistory(null)} title={`${history?.name ?? ''}: last runs`}>
        {history && <History schedule={history} />}
      </Dialog>
    </div>
  )
}

function bodyOf(s: ScheduleDto) {
  return { name: s.name, task: s.task, targetId: s.targetId, scope: s.scope, days: s.days, minuteOfDay: s.minuteOfDay, enabled: s.enabled }
}

function ScheduleForm({ schedule, targets, onDone }: { schedule: ScheduleDto | null; targets: ScheduleTargets; onDone: () => void }) {
  const [task, setTask] = useState<ScheduleTask>(schedule?.task ?? 'backup')
  const [scope, setScope] = useState(schedule?.scope ?? 'both')
  const [days, setDays] = useState(schedule?.days ?? 127)
  const save = useMutation({
    mutationFn: (body: object) => schedule ? api(`/api/schedules/${schedule.id}`, { method: 'PUT', json: body }) : api('/api/schedules', { json: body }),
    onSuccess: onDone,
  })
  const options = task === 'update-server' ? targets.installs : task === 'backup' ? targets.agents : task === 'update-mods' ? [] : targets.profiles
  const needsTarget = task !== 'update-mods' && !(task === 'backup' && scope === 'console')
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const [h, m] = String(f.get('time') ?? '04:00').split(':').map(Number)
    save.mutate({
      name: String(f.get('name') ?? ''), task, scope: task === 'backup' ? scope : null, days, minuteOfDay: h * 60 + m,
      targetId: needsTarget ? String(f.get('targetId') ?? '') || null : null, enabled: schedule?.enabled ?? true,
    })
  }
  return (
    <form className="flex flex-col gap-4" onSubmit={submit}>
      <Field label="Name" name="name" required maxLength={80} defaultValue={schedule?.name ?? ''} placeholder="Nightly backup" />
      <SelectField label="Task" value={task} onChange={e => setTask(e.target.value as ScheduleTask)}>
        {Object.entries(taskLabels).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
      </SelectField>
      {task === 'backup' && (
        <SelectField label="What" value={scope} onChange={e => setScope(e.target.value)}>
          <option value="both">The console and a server</option><option value="console">The console</option><option value="server">A server</option>
        </SelectField>
      )}
      {needsTarget && (
        <SelectField label={task === 'update-server' ? 'Server install' : task === 'backup' ? 'Server agent' : 'Server'} name="targetId" defaultValue={schedule?.targetId ?? options[0]?.id}>
          {options.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
        </SelectField>
      )}
      <fieldset className="m-0 flex flex-col gap-2 border-0 p-0">
        <legend className="mb-1 font-mono text-2xs uppercase tracking-label text-text-muted">Days</legend>
        <div className="flex flex-wrap gap-2">
          {weekdays.map(d => (
            <label key={d.bit} className={`flex min-h-11 min-w-11 cursor-pointer items-center justify-center border px-2 text-sm ${days & d.bit ? 'border-accent bg-accent font-bold text-ink' : 'border-line text-text-secondary'}`}>
              <input type="checkbox" className="sr-only" checked={!!(days & d.bit)} onChange={e => setDays(v => e.target.checked ? v | d.bit : v & ~d.bit)} />{d.label}
            </label>
          ))}
        </div>
      </fieldset>
      <Field label={`Time (${targets.timeZone})`} name="time" type="time" required defaultValue={schedule ? hhmm(schedule.minuteOfDay) : '04:00'} />
      <p className="m-0 text-sm text-text-secondary">
        {task === 'restart' || task === 'stop' ? 'Players get warnings in game at 10, 5 and 1 minutes first.'
          : task === 'update-server' || task === 'update-mods' ? 'Skipped while anyone’s on. An empty running server is stopped, backed up, updated and started again.'
          : task === 'backup' ? 'Runs even during an operation; it doesn’t touch the server.' : ''}
      </p>
      {save.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(save.error)}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant="primary" busy={save.isPending} disabled={days === 0}>Save</Button>
      </div>
    </form>
  )
}

function DeleteSchedule({ schedule, onDone }: { schedule: ScheduleDto; onDone: () => void }) {
  const del = useMutation({ mutationFn: () => api(`/api/schedules/${schedule.id}`, { method: 'DELETE' }), onSuccess: onDone })
  return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-text-secondary">Deletes <strong className="text-text">{schedule.name}</strong> and its run history. Backups it made stay.</p>
      {del.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(del.error)}</p>}
      <div className="flex flex-wrap justify-end gap-3">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="danger" busy={del.isPending} onClick={() => del.mutate()}>Delete</Button>
      </div>
    </div>
  )
}

function History({ schedule }: { schedule: ScheduleDto }) {
  const runs = useQuery({ queryKey: ['schedules', schedule.id, 'runs'], queryFn: () => api<ScheduleRunDto[]>(`/api/schedules/${schedule.id}/runs`) })
  if (runs.isPending) return <Loading />
  if (runs.error) return <ErrorNote error={runs.error} onRetry={() => runs.refetch()} />
  if (runs.data.length === 0) return <p className="m-0 text-text-muted">It hasn’t run yet.</p>
  return (
    <ul className="m-0 flex max-h-[60vh] list-none flex-col gap-2 overflow-y-auto p-0 text-sm">
      {runs.data.map(r => (
        <li key={r.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-line pb-2">
          <span className="font-mono tabular-nums">{formatWhen(r.startedAt ?? r.dueAt)}</span>
          <StatusBlock tone={outcomeTones[r.outcome]}>{r.outcome}</StatusBlock>
          {r.detail && <span className="w-full text-text-secondary">{r.detail}</span>}
        </li>
      ))}
    </ul>
  )
}
