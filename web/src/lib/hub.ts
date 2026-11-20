import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useEffect, useRef, useState, useSyncExternalStore } from 'react'
import type { JobDto, LogLine, ResourceSample, ServerDto, ServerSnapshot } from './types'

// One live connection to /hubs/ui per signed-in member. It never gives up: after a panel restart it comes back
// on its own and every mounted view re-watches what it was showing. `generation` counts (re)connections.
export type Link = 'connecting' | 'live' | 'reconnecting' | 'offline'

const delays = [0, 2000, 5000, 10000]
const delay = (attempt: number) => delays[attempt] ?? 30000

let connection: HubConnection | null = null
let state = { link: 'connecting' as Link, generation: 0 }
let attempt = 0
let retryTimer: ReturnType<typeof setTimeout> | undefined
let lastUserId: string | null = null
let users = 0 // mounted live views
const subscribers = new Set<() => void>()
const statusListeners = new Set<(s: ServerDto) => void>()
const resourceListeners = new Set<(profileId: string, r: ResourceSample) => void>()
const consoleListeners = new Set<(profileId: string, lines: LogLine[]) => void>()
const jobListeners = new Set<(job: JobDto, lines: string[]) => void>()

function set(next: Partial<typeof state>) {
  state = { ...state, ...next }
  subscribers.forEach(f => f())
}

function hub(): HubConnection {
  if (connection) return connection
  const c = new HubConnectionBuilder()
    .withUrl('/hubs/ui')
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ctx => delay(ctx.previousRetryCount) })
    .configureLogging(LogLevel.Warning)
    .build()
  c.on('status', (s: ServerDto) => statusListeners.forEach(f => f(s)))
  c.on('resources', (id: string, r: ResourceSample) => resourceListeners.forEach(f => f(id, r)))
  c.on('console', (id: string, lines: LogLine[]) => consoleListeners.forEach(f => f(id, lines)))
  c.on('job', (job: JobDto, lines: string[]) => jobListeners.forEach(f => f(job, lines)))
  c.onreconnecting(() => set({ link: 'reconnecting' }))
  c.onreconnected(() => set({ link: 'live', generation: state.generation + 1 }))
  // Closed for good (automatic reconnect only covers a lost link, not a refused or aborted one): keep trying.
  c.onclose(() => { if (connection === c) retry(c) })
  connection = c
  void start(c)
  return c
}

async function start(c: HubConnection) {
  if (c.state !== HubConnectionState.Disconnected) return
  set({ link: state.generation === 0 ? 'connecting' : 'reconnecting' })
  try {
    await c.start()
    attempt = 0
    set({ link: 'live', generation: state.generation + 1 })
  } catch {
    if (connection === c) retry(c)
  }
}

function retry(c: HubConnection) {
  set({ link: 'offline' })
  clearTimeout(retryTimer)
  retryTimer = setTimeout(() => void start(c), delay(attempt++))
}

// On sign-out, and whenever /api/me names a different member: a connection never carries one member's identity
// into another's session.
export function stopHub() {
  clearTimeout(retryTimer)
  const c = connection
  connection = null
  attempt = 0
  set({ link: 'connecting' })
  void c?.stop()
}

export function hubUser(userId: string | null | undefined) {
  if (lastUserId && userId && userId !== lastUserId) {
    stopHub()
    if (users > 0) hub()
  }
  if (userId) lastUserId = userId
}

function useHubConnection() {
  useEffect(() => {
    users++
    hub()
    return () => { users-- }
  }, [])
}

function useHubState() {
  return useSyncExternalStore(f => { subscribers.add(f); return () => subscribers.delete(f) }, () => state)
}

function describe(err: unknown): string {
  const text = err instanceof Error ? err.message : ''
  if (text.includes('forbidden')) return "You don't have access to this."
  if (text.includes('not_found')) return 'This server no longer exists.'
  return 'Lost the live connection. Trying again…'
}

export function useServerLive(profileId: string | null) {
  const { link, generation } = useHubState()
  const [server, setServer] = useState<ServerDto | null>(null)
  const [resources, setResources] = useState<ResourceSample[]>([])
  const [error, setError] = useState<string | null>(null)
  const [watched, setWatched] = useState(-1) // the connection generation the last Watch answered on
  useHubConnection()

  useEffect(() => {
    if (!profileId) return
    const onStatus = (s: ServerDto) => { if (s.id === profileId) setServer(s) }
    const onResources = (id: string, r: ResourceSample) => { if (id === profileId) setResources(prev => [...prev.slice(-449), r]) }
    statusListeners.add(onStatus)
    resourceListeners.add(onResources)
    return () => { statusListeners.delete(onStatus); resourceListeners.delete(onResources) }
  }, [profileId])

  useEffect(() => {
    if (!profileId || link !== 'live' || !connection) return
    const c = connection
    let cancelled = false
    c.invoke<ServerSnapshot>('Watch', profileId)
      .then(snap => { if (!cancelled) { setServer(snap.server); setResources(snap.resources); setError(null); setWatched(generation) } })
      .catch(err => { if (!cancelled) setError(describe(err)) })
    return () => {
      cancelled = true
      if (c.state === HubConnectionState.Connected) c.invoke('Unwatch', profileId).catch(() => {})
    }
  }, [profileId, link, generation])

  // After a reconnect, what we hold is from before it: not live until Watch answers again.
  return { server, resources, link: link === 'live' && watched !== generation ? 'connecting' as Link : link, error }
}

export function useConsole(profileId: string | null, enabled: boolean) {
  const { link, generation } = useHubState()
  const [lines, setLines] = useState<LogLine[]>([])
  const [error, setError] = useState<string | null>(null)
  useHubConnection()

  useEffect(() => {
    if (!profileId || !enabled) return
    const onLines = (id: string, more: LogLine[]) => { if (id === profileId) setLines(prev => [...prev, ...more].slice(-2000)) }
    consoleListeners.add(onLines)
    return () => { consoleListeners.delete(onLines) }
  }, [profileId, enabled])

  useEffect(() => {
    if (!profileId || !enabled || link !== 'live' || !connection) return
    let cancelled = false
    connection.invoke<LogLine[]>('WatchConsole', profileId)
      .then(history => { if (!cancelled) { setLines(history); setError(null) } })
      .catch(err => { if (!cancelled) setError(describe(err)) })
    return () => { cancelled = true }
  }, [profileId, enabled, link, generation])

  return { lines, error }
}

// Live job progress (Phase 5a): every job's changes, with the new log lines since the last event.
export function useJobsLive(enabled: boolean, onJob: (job: JobDto, lines: string[]) => void) {
  const { link, generation } = useHubState()
  const handler = useRef(onJob)
  handler.current = onJob
  useHubConnection()

  useEffect(() => {
    const f = (job: JobDto, lines: string[]) => handler.current(job, lines)
    jobListeners.add(f)
    return () => { jobListeners.delete(f) }
  }, [])

  useEffect(() => {
    if (!enabled || link !== 'live' || !connection) return
    connection.invoke('WatchJobs').catch(() => {})
  }, [enabled, link, generation])

  return link
}
