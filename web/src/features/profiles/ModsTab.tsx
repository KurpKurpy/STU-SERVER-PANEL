import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { Loading } from '../../components/Feedback'
import { SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { CollectionDto } from '../../lib/types'
import { useCollections } from '../mods/CollectionsPage'
import { useProfile } from './ProfileLayout'

// The profile's mod list is part of its revision (Phase 5a Ruling 7): save and apply it like any other setting.
export function ModsTab() {
  const { draft } = useProfile()
  const can = useCan()
  const readOnly = !can('profiles.manage')
  const lists = useCollections(can('mods.view'))
  const listId = draft.snapshot!.modCollectionId ?? ''
  const list = useQuery({ queryKey: ['collections', listId], queryFn: () => api<CollectionDto>(`/api/collections/${listId}`), enabled: !!listId && can('mods.view') })
  const enabled = list.data?.items.filter(i => i.enabled) ?? []
  const group = (pick: (i: CollectionDto['items'][number]) => boolean) => enabled.filter(pick).map(i => i.name)
  const notInstalled = enabled.filter(i => !i.optional && i.modStatus !== 'Installed')

  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <Panel title="Mod list">
        <div className="flex flex-col gap-4 px-6 py-5">
          {!can('mods.view') ? <p className="m-0 text-text-muted">You can't see mod lists.</p> : lists.isPending ? <Loading /> : (
            <SelectField label="Mods this server loads" value={listId} disabled={readOnly}
              onChange={e => draft.update(s => { s.modCollectionId = e.target.value || null })}>
              <option value="">None (vanilla)</option>
              {lists.data?.map(l => <option key={l.id} value={l.id}>{l.name} ({l.count} mods)</option>)}
            </SelectField>
          )}
          <p className="m-0 text-sm text-text-secondary">
            Save and apply to use it. Start then loads the list's current mods and deploys their keys first. Edit lists on the{' '}
            <Link to="/mods/lists">Mod lists</Link> page.
          </p>
          {notInstalled.length > 0 && (
            <p role="status" className="m-0 border border-warn/40 bg-black/20 px-4 py-3 text-sm">
              Not installed yet, so Start will refuse: {notInstalled.map(i => i.name).join(', ')}. Download them on the <Link to="/mods">Mods</Link> page.
            </p>
          )}
        </div>
      </Panel>
      {list.data && (
        <Panel title="What loads" meta={<Link to={`/mods/lists/${list.data.id}`}>Edit {list.data.name}</Link>}>
          <dl className="m-0 flex flex-col gap-4 px-6 py-5">
            {[
              ['Server and players (-mod)', group(i => i.client)],
              ['Server only (-serverMod)', group(i => i.serverOnly)],
              ['Headless clients', group(i => i.headless)],
              ['Optional for players', group(i => i.optional)],
            ].map(([label, names]) => (
              <div key={label as string} className="flex flex-col gap-1">
                <dt className="text-2xs font-semibold uppercase tracking-label text-text-muted">{label as string}</dt>
                <dd className="m-0 text-sm">{(names as string[]).length ? (names as string[]).join(', ') : <span className="text-text-muted">None</span>}</dd>
              </div>
            ))}
            {enabled.length < (list.data.items.length) && (
              <p className="m-0 text-sm text-text-muted"><StatusBlock tone="off">{list.data.items.length - enabled.length} off</StatusBlock> Disabled mods don't load.</p>
            )}
          </dl>
        </Panel>
      )}
    </div>
  )
}
