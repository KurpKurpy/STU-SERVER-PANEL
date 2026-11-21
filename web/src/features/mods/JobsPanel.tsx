import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { formatWhen } from '../../components/Feedback'
import { Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { JobDto, JobStatus, SteamPromptKind } from '../../lib/types'
import { isOpen } from './useJobs'

const labels: Record<string, string> = {
  'workshop.download': 'Downloading mods',
  'workshop.remove': 'Removing mod files',
  'server.install': 'Installing or updating the server',
}

const tones: Record<JobStatus, 'ok' | 'warn' | 'bad' | 'off'> = {
  Queued: 'off', Running: 'warn', NeedsInput: 'bad', Succeeded: 'ok', Failed: 'bad', Cancelled: 'off', Interrupted: 'off',
}

const statusText: Record<JobStatus, string> = {
  Queued: 'Queued', Running: 'Running', NeedsInput: 'Steam is asking', Succeeded: 'Done', Failed: 'Failed', Cancelled: 'Cancelled', Interrupted: 'Interrupted',
}

// A real measurement when there is one, otherwise an indeterminate bar (design §2: never invent progress).
export function ProgressBar({ value }: { value: number | null }) {
  if (value === null)
    return (
      <div role="progressbar" aria-label="Progress" aria-valuetext="Working" className="h-1.5 w-full overflow-hidden bg-white/10">
        <div className="h-full w-1/3 animate-pulse bg-accent" />
      </div>
    )
  const pct = Math.round(value * 100)
  return (
    <div role="progressbar" aria-label="Progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct} className="h-1.5 w-full bg-white/10">
      <div className="h-full bg-accent transition-[width] duration-500" style={{ width: `${pct}%` }} />
    </div>
  )
}

const promptText: Record<SteamPromptKind, { label: string; hint: string }> = {
  Password: { label: 'Steam password', hint: 'For the Steam account set on this page. It goes to SteamCMD once and is never stored.' },
  EmailCode: { label: 'Steam Guard code from email', hint: "Steam emailed a code to the account's address." },
  TwoFactorCode: { label: 'Steam Guard code', hint: 'The code shown in the Steam mobile app.' },
  MobileConfirm: { label: '', hint: 'Approve the sign-in in the Steam mobile app. The job carries on by itself.' },
}

function SteamAnswer({ job }: { job: JobDto }) {
  const [value, setValue] = useState('')
  const answer = useMutation({
    mutationFn: () => api(`/api/jobs/${job.id}/answer`, { json: { answer: value } }),
    onSuccess: () => setValue(''),
  })
  const text = promptText[job.prompt!]
  if (job.prompt === 'MobileConfirm') return <p role="status" className="m-0 border border-warn/40 bg-black/20 px-4 py-3 text-sm">{text.hint}</p>
  function submit(e: FormEvent) {
    e.preventDefault()
    answer.mutate()
  }
  return (
    <form onSubmit={submit} className="flex flex-col gap-2 border border-warn/40 bg-black/20 px-4 py-3" aria-label="Steam sign-in">
      <Field label={text.label} hint={text.hint} value={value} required maxLength={256} autoFocus
        type={job.prompt === 'Password' ? 'password' : 'text'} autoComplete={job.prompt === 'Password' ? 'new-password' : 'one-time-code'}
        error={answer.error ? errorMessage(answer.error) : undefined} onChange={e => setValue(e.target.value)} />
      <Button type="submit" variant="primary" size="sm" className="self-start" busy={answer.isPending}>Send to SteamCMD</Button>
    </form>
  )
}

export function JobsPanel({ jobs, tails, types }: { jobs: JobDto[]; tails: Record<string, string[]>; types?: string[] }) {
  const can = useCan()
  const mayAnswer = can('mods.install') || can('server.update')
  const queryClient = useQueryClient()
  const cancel = useMutation({ mutationFn: (id: string) => api(`/api/jobs/${id}/cancel`, { method: 'POST' }) })
  const mine = jobs.filter(j => !types || types.includes(j.type))
  // Clear history: the finished jobs of the kinds this panel shows; the server keeps open ones and ones you may not run.
  const clear = useMutation({
    mutationFn: () => api('/api/jobs/clear', { json: { types: types ?? [...new Set(mine.map(j => j.type))] } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  })
  const shown = [...mine.filter(isOpen), ...mine.filter(j => !isOpen(j)).slice(0, 4)]
  if (shown.length === 0) return null
  return (
    <Panel title="Jobs" meta={
      <>
        <span>SteamCMD runs one job at a time</span>
        {mayAnswer && mine.some(j => !isOpen(j)) && <Button size="sm" busy={clear.isPending} onClick={() => clear.mutate()}>Clear finished</Button>}
      </>
    }>
      <ul className="m-0 flex list-none flex-col gap-0 p-0" aria-label="Jobs">
        {shown.map(job => {
          const tail = tails[job.id] ?? (job.logText ? job.logText.split('\n') : [])
          return (
            <li key={job.id} className="flex flex-col gap-2 border-t border-line px-6 py-4 first:border-t-0">
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-semibold">{labels[job.type] ?? job.type}</span>
                <StatusBlock tone={tones[job.status]}>{statusText[job.status]}</StatusBlock>
                <span className="text-sm text-text-muted">{formatWhen(job.createdAt)}</span>
                {isOpen(job) && mayAnswer && (
                  <Button size="sm" className="ml-auto" busy={cancel.isPending && cancel.variables === job.id} onClick={() => cancel.mutate(job.id)}>Cancel</Button>
                )}
              </div>
              {isOpen(job) && <ProgressBar value={job.progress} />}
              {isOpen(job) && tail.length > 0 && <p className="m-0 truncate font-mono text-xs text-text-muted">{tail[tail.length - 1]}</p>}
              {job.error && <p className="m-0 text-sm text-accent-text">{job.error}</p>}
              {job.status === 'NeedsInput' && job.prompt && mayAnswer && <SteamAnswer job={job} />}
            </li>
          )
        })}
      </ul>
      {cancel.error && <p role="alert" className="m-0 px-6 pb-4 text-sm text-accent-text">{errorMessage(cancel.error)}</p>}
      {clear.error && <p role="alert" className="m-0 px-6 pb-4 text-sm text-accent-text">{errorMessage(clear.error)}</p>}
    </Panel>
  )
}
