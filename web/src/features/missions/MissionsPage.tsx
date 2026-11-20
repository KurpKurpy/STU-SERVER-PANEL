import { useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading, NoAccess, formatWhen } from '../../components/Feedback'
import { Check, Field, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { useCan } from '../../lib/auth'
import { UploadForm } from './UploadDialog'
import { categories, useMissionList, type MissionFilter } from './useMissions'

const th = 'whitespace-nowrap px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-t border-line px-4 py-2.5'

export function MissionsPage() {
  const can = useCan()
  const navigate = useNavigate()
  const [filter, setFilter] = useState<MissionFilter>({ q: '', map: '', category: '', tag: '', archived: false })
  const [uploading, setUploading] = useState(false)
  const missions = useMissionList(filter, can('missions.view'))
  const all = useMissionList({ q: '', map: '', category: '', tag: '', archived: false }, can('missions.view')) // for the map and tag choices
  if (!can('missions.view')) return <NoAccess />
  const maps = [...new Set((all.data ?? []).map(m => m.map))].sort((a, b) => a.localeCompare(b))
  const tags = [...new Set((all.data ?? []).flatMap(m => m.tags))].sort((a, b) => a.localeCompare(b))
  const set = (patch: Partial<MissionFilter>) => setFilter(f => ({ ...f, ...patch }))

  return (
    <>
      <Panel title="Mission library" meta={can('missions.create') && <Button size="sm" variant="primary" onClick={() => setUploading(true)}>Upload mission</Button>}>
        <div className="flex flex-wrap items-end gap-3 px-6 py-4">
          <Field label="Search" placeholder="Name, class name or description" value={filter.q} onChange={e => set({ q: e.target.value })} className="min-w-56 flex-1" />
          <SelectField label="Map" value={filter.map} onChange={e => set({ map: e.target.value })} className="min-w-36">
            <option value="">Any map</option>
            {maps.map(m => <option key={m} value={m}>{m}</option>)}
          </SelectField>
          <SelectField label="Category" value={filter.category} onChange={e => set({ category: e.target.value })} className="min-w-44">
            <option value="">Any category</option>
            {categories.map(c => <option key={c} value={c}>{c}</option>)}
          </SelectField>
          <Check label="Archived" checked={filter.archived} onChange={e => set({ archived: e.target.checked })} />
        </div>
        {tags.length > 0 && (
          <div className="flex flex-wrap gap-2 px-6 pb-4" role="group" aria-label="Filter by tag">
            {tags.map(t => (
              <button key={t} type="button" aria-pressed={filter.tag === t} onClick={() => set({ tag: filter.tag === t ? '' : t })}
                className={`border px-2.5 py-1 text-xs ${filter.tag === t ? 'border-accent bg-accent/15 text-text' : 'border-line-strong text-text-secondary hover:border-text-muted'}`}>
                {t}
              </button>
            ))}
          </div>
        )}
        {missions.isPending && <Loading />}
        {missions.error && <ErrorNote error={missions.error} onRetry={() => missions.refetch()} />}
        {missions.data?.length === 0 && (
          <p className="m-0 border-t border-line px-6 py-5 text-text-muted">
            {filter.q || filter.map || filter.category || filter.tag ? 'No mission matches.' : filter.archived ? 'No archived missions.' : 'No missions yet.'}
          </p>
        )}
        {!!missions.data?.length && (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-sm">
              <thead><tr><th className={th}>Mission</th><th className={th}>Map</th><th className={th}>Category</th><th className={th}>Version</th><th className={th}>Author</th><th className={th}>Changed</th></tr></thead>
              <tbody>
                {missions.data.map(m => (
                  <tr key={m.id} className="cursor-pointer hover:bg-white/[.03]" onClick={() => navigate(`/missions/${m.id}`)}>
                    <td className={td}>
                      <Link to={`/missions/${m.id}`} className="font-semibold text-text hover:text-accent-text" onClick={e => e.stopPropagation()}>{m.displayName}</Link>
                      <div className="break-all font-mono text-xs text-text-muted">{m.className}</div>
                      {m.tags.length > 0 && <div className="mt-1 flex flex-wrap gap-1">{m.tags.map(t => <span key={t} className="border border-line px-1.5 text-2xs text-text-secondary">{t}</span>)}</div>}
                    </td>
                    <td className={td}>{m.map}</td>
                    <td className={`${td} text-text-secondary`}>{m.category ?? '—'}</td>
                    <td className={`${td} tabular-nums`}>{m.currentVersion ? `v${m.currentVersion}` : '—'}</td>
                    <td className={`${td} text-text-secondary`}>{m.author?.name ?? '—'}</td>
                    <td className={`${td} whitespace-nowrap text-text-secondary`}>{formatWhen(m.updatedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <Dialog open={uploading} onClose={() => setUploading(false)} title="Upload a mission">
        {uploading && <UploadForm onDone={id => { setUploading(false); navigate(`/missions/${id}`) }} />}
      </Dialog>
    </>
  )
}
