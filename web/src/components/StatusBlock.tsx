import type { ReactNode } from 'react'

export type Tone = 'ok' | 'warn' | 'bad' | 'off'

const tones: Record<Tone, string> = { ok: 'bg-ok', warn: 'bg-warn', bad: 'bg-bad', off: 'bg-off' }

// Status is always a word on a solid block (the order-of-battle "Eligible" style), never colour alone.
export function StatusBlock({ tone, children }: { tone: Tone; children: ReactNode }) {
  return (
    <span className={`${tones[tone]} inline-block whitespace-nowrap px-2 py-[3px] text-xs font-bold uppercase tracking-[.08em] text-ink`}>
      {children}
    </span>
  )
}

export function RoleChip({ children }: { children: ReactNode }) {
  return (
    <span className="whitespace-nowrap border border-line-strong px-[7px] py-0.5 text-xs font-semibold uppercase tracking-[.06em] text-text-secondary">
      {children}
    </span>
  )
}
