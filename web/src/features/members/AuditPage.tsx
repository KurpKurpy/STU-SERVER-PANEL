import { useInfiniteQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '../../components/Button'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { api } from '../../lib/api'
import type { AuditDto } from '../../lib/types'

const pageSize = 50
const filters = [
  { value: '', label: 'Everything' },
  { value: 'auth.', label: 'Sign-in and passwords' },
  { value: 'member.', label: 'Members and invites' },
  { value: 'role.', label: 'Roles' },
  { value: 'announcement.', label: 'Announcements' },
  { value: 'settings.', label: 'Settings' },
  { value: 'setup.', label: 'First-time setup' },
]
const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-2'

export function AuditPage() {
  const [action, setAction] = useState('')
  const audit = useInfiniteQuery({
    queryKey: ['audit', action],
    initialPageParam: '',
    queryFn: ({ pageParam }) => {
      const q = new URLSearchParams({ limit: String(pageSize) })
      if (action) q.set('action', action)
      if (pageParam) q.set('before', pageParam)
      return api<AuditDto[]>(`/api/audit?${q}`)
    },
    getNextPageParam: last => (last.length === pageSize ? last[last.length - 1].at : undefined),
  })
  const rows = audit.data?.pages.flat() ?? []

  return (
    <Panel title="Audit log" meta="every change, who and when">
      <div className="flex flex-wrap items-end gap-4 border-b border-line px-6 py-4">
        <SelectField label="Show" value={action} onChange={e => setAction(e.target.value)} className="w-72 max-w-full">
          {filters.map(f => <option key={f.value} value={f.value}>{f.label}</option>)}
        </SelectField>
      </div>
      {audit.isPending && <div className="px-6 py-5"><Loading /></div>}
      {audit.error && <div className="px-6 py-5"><ErrorNote error={audit.error} onRetry={() => audit.refetch()} /></div>}
      {audit.data && rows.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">Nothing recorded yet.</p>}
      {rows.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead><tr><th className={th}>When</th><th className={th}>Who</th><th className={th}>Action</th><th className={th}>What happened</th><th className={th}>IP</th></tr></thead>
            <tbody>
              {rows.map(r => (
                <tr key={r.id}>
                  <td className={`${td} whitespace-nowrap font-mono text-text-muted`}>{formatWhen(r.at)}</td>
                  <td className={`${td} font-semibold`}>{r.actor ?? 'System'}</td>
                  <td className={`${td} whitespace-nowrap font-mono text-xs text-text-muted`}>{r.action}</td>
                  <td className={td}>{r.summary}</td>
                  <td className={`${td} whitespace-nowrap font-mono text-xs text-text-muted`}>{r.ip ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {audit.hasNextPage && (
        <div className="px-6 py-4">
          <Button size="sm" busy={audit.isFetchingNextPage} onClick={() => audit.fetchNextPage()}>Load older</Button>
        </div>
      )}
    </Panel>
  )
}
