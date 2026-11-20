import { ArrowDownIcon, ArrowUpIcon } from '@phosphor-icons/react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Button } from '../../components/Button'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { api, errorMessage } from '../../lib/api'
import type { ChecklistItemDto } from '../../lib/types'

interface Draft { id: string | null; label: string; autoKey: string | null; enabled: boolean; delete: boolean }

// Phase 6a Ruling 8: the unit's operations checklist. Automatic items can be renamed, moved and switched off; manual ones also deleted.
export function ChecklistSettings({ className = '' }: { className?: string }) {
  const queryClient = useQueryClient()
  const items = useQuery({ queryKey: ['checklist'], queryFn: () => api<ChecklistItemDto[]>('/api/checklist') })
  const [draft, setDraft] = useState<Draft[]>([])
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { if (items.data) setDraft(items.data.map(i => ({ id: i.id, label: i.label, autoKey: i.autoKey, enabled: i.enabled, delete: false }))) }, [items.data])

  const set = (i: number, change: Partial<Draft>) => setDraft(d => d.map((x, j) => (j === i ? { ...x, ...change } : x)))
  const move = (i: number, by: number) => setDraft(d => { const n = [...d]; const [x] = n.splice(i, 1); n.splice(i + by, 0, x); return n })
  async function save() {
    setBusy(true)
    setMessage(null)
    try {
      await api('/api/checklist', { method: 'PUT', json: { items: draft.map((x, i) => ({ id: x.id, label: x.label, order: i + 1, enabled: x.enabled, delete: x.delete })) } })
      await queryClient.invalidateQueries({ queryKey: ['checklist'] })
      setMessage({ ok: true, text: 'Checklist saved.' })
    } catch (e) { setMessage({ ok: false, text: errorMessage(e) }) } finally { setBusy(false) }
  }

  return (
    <Panel title="Operations checklist" className={className} meta={<Button size="sm" variant="primary" busy={busy} onClick={save}>Save</Button>}>
      {items.isPending && <div className="px-6 py-5"><Loading /></div>}
      {items.error && <div className="px-6 py-5"><ErrorNote error={items.error} onRetry={() => items.refetch()} /></div>}
      <p className="m-0 px-6 pt-4 text-sm text-text-secondary">Prepare checks the automatic items itself; manual ones are ticked on each operation.</p>
      <ul className="m-0 mt-2 list-none p-0">
        {draft.map((x, i) => !x.delete && (
          <li key={x.id ?? `new-${i}`} className="flex flex-wrap items-center gap-2 border-t border-line px-6 py-2">
            <input aria-label="Item label" value={x.label} maxLength={120} onChange={e => set(i, { label: e.target.value })}
              className="min-h-10 min-w-0 flex-1 border border-line-strong bg-console px-3 text-base text-text" />
            <span className="w-20 text-xs text-text-muted">{x.autoKey ? 'Automatic' : 'Manual'}</span>
            <label className="flex min-h-10 items-center gap-1.5 text-sm"><input type="checkbox" checked={x.enabled} onChange={e => set(i, { enabled: e.target.checked })} />On</label>
            <Button size="sm" aria-label={`Move ${x.label} up`} disabled={i === 0} onClick={() => move(i, -1)}><ArrowUpIcon size={16} aria-hidden /></Button>
            <Button size="sm" aria-label={`Move ${x.label} down`} disabled={i === draft.length - 1} onClick={() => move(i, 1)}><ArrowDownIcon size={16} aria-hidden /></Button>
            {!x.autoKey && <Button size="sm" variant="danger" onClick={() => (x.id ? set(i, { delete: true }) : setDraft(d => d.filter((_, j) => j !== i)))}>Remove</Button>}
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap items-center gap-3 border-t border-line px-6 py-3">
        <Button size="sm" onClick={() => setDraft(d => [...d, { id: null, label: 'New manual item', autoKey: null, enabled: true, delete: false }])}>Add a manual item</Button>
        {message && <span role={message.ok ? 'status' : 'alert'} className={`text-sm ${message.ok ? 'text-text-secondary' : 'text-accent-text'}`}>{message.text}</span>}
      </div>
    </Panel>
  )
}
