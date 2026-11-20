import { StatusBlock } from '../../components/StatusBlock'
import type { CompatResult, CompatStatus, ScanState } from '../../lib/types'

export const compatTone = { Ready: 'ok', Unknown: 'warn', Missing: 'bad' } as const
export const compatText: Record<CompatStatus, string> = { Ready: 'Ready', Unknown: "Couldn't confirm", Missing: "Doesn't match" }

export function CompatChip({ status }: { status: CompatStatus }) {
  return <StatusBlock tone={compatTone[status]}>{compatText[status]}</StatusBlock>
}

// Why a mission doesn't match: its map, then its missing addons grouped by the mod that has them (or Eden's name for it).
export function CompatReasons({ r }: { r: CompatResult }) {
  if (r.status === 'Ready') return null
  const groups = new Map<string, string[]>()
  for (const m of r.missing) {
    const key = m.libraryMod ? `${m.libraryMod} (in the library, not in this list)` : m.edenMod ? `${m.edenMod} (not in the library)` : 'Mod not recorded by Eden'
    groups.set(key, [...(groups.get(key) ?? []), m.addon])
  }
  return (
    <ul className="m-0 mt-1.5 flex list-none flex-col gap-1 p-0 text-xs text-text-secondary">
      {r.missingMap && <li>Map <strong className="text-text">{r.missingMap}</strong> isn't on the server or in this list.</li>}
      {[...groups].map(([mod, addons]) => (
        <li key={mod}><span className="text-text">{mod}</span>: <span className="break-all font-mono">{addons.slice(0, 6).join(', ')}{addons.length > 6 ? ` and ${addons.length - 6} more` : ''}</span></li>
      ))}
      {r.status === 'Unknown' && <li>Couldn't read all of {r.unreadableMods.join(', ')}, which may provide these.</li>}
    </ul>
  )
}

const scanTone = { Scanned: 'ok', Partial: 'warn', NotScanned: 'off', Failed: 'bad' } as const
const scanText: Record<ScanState, string> = { Scanned: 'Scanned', Partial: 'Partly scanned', NotScanned: 'Not scanned', Failed: 'Scan failed' }

// Whether the panel knows which addons and maps something provides; the note lists what couldn't be read.
export function ScanChip({ status, note }: { status: ScanState; note: string | null }) {
  return (
    <span title={note ?? undefined}>
      <StatusBlock tone={scanTone[status]}>{scanText[status]}</StatusBlock>
    </span>
  )
}
