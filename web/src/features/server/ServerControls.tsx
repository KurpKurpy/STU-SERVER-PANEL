import { ArrowClockwiseIcon, PlayIcon, PowerIcon, StopIcon } from '@phosphor-icons/react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import { isRunning } from '../../lib/format'
import type { ServerDto } from '../../lib/types'

type Action = 'start' | 'stop' | 'restart' | 'force-stop'

const actions: Record<Action, { label: string; verb: string; what: string; icon: typeof PlayIcon }> = {
  start: { label: 'Start', verb: 'Start server', icon: PlayIcon,
    what: "The agent launches the server with this profile's settings. It shows as Starting until it answers queries, then Online." },
  restart: { label: 'Restart', verb: 'Restart server', icon: ArrowClockwiseIcon,
    what: 'The server is asked to close, then starts again with the current profile settings.' },
  stop: { label: 'Stop', verb: 'Stop server', icon: StopIcon,
    what: "The server is asked to close. If it hasn't closed when the agent's timeout runs out (60 seconds unless changed), it is killed." },
  'force-stop': { label: 'Force stop', verb: 'Force stop', icon: PowerIcon,
    what: 'The server process is killed at once, without a chance to save. Use it when a normal stop hangs.' },
}

// Start for a stopped server; Restart and Stop while it runs; Force stop in any running state, stopping included.
// Each sits behind its permission and asks first, saying what will happen and who it affects.
export function ServerControls({ server }: { server: ServerDto }) {
  const can = useCan()
  const [open, setOpen] = useState<Action | null>(null)
  const s = server.state
  const shown: Action[] = []
  if (can('server.control') && (s === 'offline' || s === 'error')) shown.push('start')
  if (can('server.control') && (s === 'starting' || s === 'online' || s === 'notResponding')) shown.push('restart', 'stop')
  if (can('server.forceStop') && isRunning(s)) shown.push('force-stop')
  if (shown.length === 0) return null
  // Start launches the applied revision (Phase 3), so a profile that was never applied can't start yet.
  const notApplied = server.appliedRevision === null

  return (
    <div className="flex flex-wrap items-center gap-2">
      {shown.map(a => {
        const { label, icon: Icon } = actions[a]
        return (
          <Button key={a} size="sm" variant={a === 'start' ? 'primary' : a === 'force-stop' ? 'danger' : 'default'}
            disabled={a === 'start' && notApplied} title={a === 'start' && notApplied ? 'Apply the profile first' : undefined} onClick={() => setOpen(a)}>
            <Icon size={15} aria-hidden="true" />{label}
          </Button>
        )
      })}
      {shown.includes('start') && notApplied && <span className="text-sm text-text-muted">Apply the profile first</span>}
      <Dialog open={open !== null} onClose={() => setOpen(null)} title={open ? `${actions[open].label} ${server.name}?` : ''}>
        {open && <Confirm server={server} action={open} onDone={() => setOpen(null)} />}
      </Dialog>
    </div>
  )
}

function Confirm({ server, action, onDone }: { server: ServerDto; action: Action; onDone: () => void }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const run = useMutation({
    mutationFn: () => api(`/api/servers/${server.id}/${action}`, { method: 'POST' }),
    onSuccess: async () => {
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['servers'] }), queryClient.invalidateQueries({ queryKey: ['activity'] })])
      onDone()
    },
    onError: err => setError(errorMessage(err)),
  })
  const players = server.query?.players ?? 0
  const affects = action !== 'start' && players > 0

  return (
    <>
      <p className="m-0 max-w-prose text-text-secondary">{actions[action].what}</p>
      {affects && (
        <p className="m-0 font-semibold text-warn">{players} player{players === 1 ? '' : 's'} online will be disconnected.</p>
      )}
      {error && <p role="alert" className="m-0 text-accent-text">{error}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="primary" busy={run.isPending} onClick={() => { setError(null); run.mutate() }}>{actions[action].verb}</Button>
      </div>
    </>
  )
}
