import type { ServerState } from './types'
import type { Tone } from '../components/StatusBlock'

export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let v = n / 1024
  let i = 0
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v >= 100 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`
}

export const formatRate = (bytesPerSec: number) => `${formatBytes(bytesPerSec)}/s`

export function formatDuration(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000))
  const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60
  const pad = (x: number) => String(x).padStart(2, '0')
  return d > 0 ? `${d}d ${h}h ${pad(m)}m` : h > 0 ? `${h}h ${pad(m)}m ${pad(sec)}s` : `${m}m ${pad(sec)}s`
}

export const stateLabel: Record<ServerState, string> = {
  offline: 'Offline', starting: 'Starting', online: 'Online', notResponding: 'Not responding',
  stopping: 'Stopping', error: 'Error', unknown: 'Agent offline',
}

export const stateTone: Record<ServerState, Tone> = {
  online: 'ok', starting: 'warn', stopping: 'warn', notResponding: 'warn', error: 'bad', offline: 'off', unknown: 'off',
}

export const isRunning = (s: ServerState) => s === 'starting' || s === 'online' || s === 'notResponding' || s === 'stopping'

// "co40_Kestrel · Altis"; with no mission loaded the server reports only its state (e.g. "Waiting").
export function missionAndMap(q: { mission: string; map: string }): string {
  return [q.mission, q.map].filter(Boolean).join(' · ')
}
