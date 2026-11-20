import { ArrowDownIcon, ArrowUpIcon, XIcon } from '@phosphor-icons/react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { Button } from '../../components/Button'
import { ErrorNote, Loading, NoAccess } from '../../components/Feedback'
import { Field, SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { CollectionDto, CollectionItemDto } from '../../lib/types'
import { useCollections } from './CollectionsPage'
import { modStatusText, modTone, useMods, workshopUrl } from './ModsPage'

type Flag = 'client' | 'serverOnly' | 'headless' | 'optional'
const flags: { key: Flag; label: string; hint: string }[] = [
  { key: 'client', label: 'Client', hint: 'Loaded by the server and every player (-mod)' },
  { key: 'serverOnly', label: 'Server only', hint: 'Loaded by the server only (-serverMod)' },
  { key: 'headless', label: 'Headless', hint: 'Loaded by headless clients' },
  { key: 'optional', label: 'Optional', hint: 'Not loaded by the server; its keys are deployed so players may use it' },
]

// The same rules the server checks: Client and Server only exclude each other, and Optional excludes the rest.
function setFlag(item: CollectionItemDto, flag: Flag, on: boolean): CollectionItemDto {
  const next = { ...item, [flag]: on }
  if (on && flag === 'client') next.serverOnly = next.optional = false
  if (on && flag === 'serverOnly') next.client = next.optional = false
  if (on && flag === 'headless') next.optional = false
  if (on && flag === 'optional') next.client = next.serverOnly = next.headless = false
  return next
}

export function CollectionEditor() {
  const { id } = useParams()
  const can = useCan()
  const list = useQuery({ queryKey: ['collections', id], queryFn: () => api<CollectionDto>(`/api/collections/${id}`), enabled: can('mods.view') })
  if (!can('mods.view')) return <NoAccess />
  if (list.isPending) return <Loading />
  if (list.error) return <ErrorNote error={list.error} onRetry={() => list.refetch()} />
  return <Editor key={list.data.updatedAt} list={list.data} />
}

function Editor({ list }: { list: CollectionDto }) {
  const can = useCan()
  const manage = can('collections.manage')
  const queryClient = useQueryClient()
  const mods = useMods()
  const others = useCollections()
  const [name, setName] = useState(list.name)
  const [description, setDescription] = useState(list.description)
  const [items, setItems] = useState(list.items)
  const [adding, setAdding] = useState('')
  const [copyFrom, setCopyFrom] = useState('')
  const dirty = name !== list.name || description !== list.description || JSON.stringify(items) !== JSON.stringify(list.items)
  const save = useMutation({
    mutationFn: () => api<CollectionDto>(`/api/collections/${list.id}`, {
      method: 'PUT',
      json: { name, description, items: items.map(({ modId, client, serverOnly, headless, optional, enabled }) => ({ modId, client, serverOnly, headless, optional, enabled })) },
    }),
    onSuccess: () => Promise.all(['collections', 'mods'].map(k => queryClient.invalidateQueries({ queryKey: [k] }))),
  })
  const install = useMutation({ mutationFn: (ids: string[]) => api('/api/mods/install', { json: { modIds: ids, mode: 'install' } }) })

  const update = (i: number, change: (x: CollectionItemDto) => CollectionItemDto) => setItems(xs => xs.map((x, j) => (j === i ? change(x) : x)))
  const move = (i: number, by: number) => setItems(xs => {
    const next = [...xs]
    const [x] = next.splice(i, 1)
    next.splice(i + by, 0, x)
    return next
  })
  const available = (mods.data ?? []).filter(m => !items.some(i => i.modId === m.id))
  function add(modId: string) {
    const m = mods.data?.find(x => x.id === modId)
    if (!m) return
    setItems(xs => [...xs, { modId: m.id, name: m.name, workshopId: m.workshopId, localFolder: m.localFolder, modStatus: m.status, updateAvailable: m.updateAvailable,
      client: true, serverOnly: false, headless: false, optional: false, enabled: true }])
    setAdding('')
  }
  async function copy() {
    const source = await api<CollectionDto>(`/api/collections/${copyFrom || others.data?.find(o => o.id !== list.id)?.id}`)
    setItems(xs => [...xs, ...source.items.filter(s => !xs.some(x => x.modId === s.modId))])
  }
  const notInstalled = items.filter(i => i.enabled && !i.optional && i.modStatus !== 'Installed')
  const otherLists = (others.data ?? []).filter(o => o.id !== list.id)

  return (
    <div className="flex flex-col gap-4">
      <Panel title={list.name} meta={
        <>
          <Link to="/mods/lists">All mod lists</Link>
          <a href={`/api/collections/${list.id}/export`} download>Export preset</a>
        </>
      }>
        <div className="flex flex-col gap-4 px-6 py-5">
          {list.usedBy.length > 0 && (
            <p className="m-0 text-sm text-text-secondary">Used by {list.usedBy.join(', ')}. A change applies at their next Start or Restart; running servers keep what they loaded.</p>
          )}
          <div className="grid grid-cols-1 gap-3 md:grid-cols-[minmax(0,1fr)_minmax(0,2fr)]">
            <Field label="Name" value={name} maxLength={60} disabled={!manage} onChange={e => setName(e.target.value)} />
            <Field label="Description" value={description} maxLength={500} disabled={!manage} onChange={e => setDescription(e.target.value)} />
          </div>
          {notInstalled.length > 0 && (
            <div role="status" className="flex flex-wrap items-center gap-3 border border-warn/40 bg-black/20 px-4 py-3 text-sm">
              <span>{notInstalled.length} mods aren't installed, so a server can't start with this list yet.</span>
              {can('mods.install') && (
                <Button size="sm" busy={install.isPending} onClick={() => install.mutate(notInstalled.filter(i => i.workshopId).map(i => i.modId))}>Download them</Button>
              )}
              {install.isSuccess && <Link to="/mods">See the download</Link>}
            </div>
          )}
          {manage && (
            <div className="flex flex-wrap gap-2" role="toolbar" aria-label="Bulk actions">
              <Button size="sm" onClick={() => setItems(xs => xs.map(x => ({ ...x, enabled: true })))}>Enable all</Button>
              <Button size="sm" onClick={() => setItems(xs => xs.map(x => ({ ...x, enabled: false })))}>Disable all</Button>
              <Button size="sm" onClick={() => setItems(xs => xs.map(x => (x.client ? setFlag(x, 'headless', true) : x)))}>Copy Client to Headless</Button>
              <Button size="sm" onClick={() => setItems(xs => [...xs].sort((a, b) => a.name.localeCompare(b.name)))}>Sort by name</Button>
              <Button size="sm" onClick={() => setItems(xs => [...xs].sort((a, b) => (a.workshopId ?? Infinity) - (b.workshopId ?? Infinity)))}>Sort by Workshop ID</Button>
              <Button size="sm" variant="danger" onClick={() => setItems([])}>Clear</Button>
            </div>
          )}
        </div>
        {items.length === 0 ? <p className="m-0 px-6 pb-5 text-text-muted">No mods in this list.</p> : (
          <div className="relative overflow-x-auto">
            <table className="w-full border-collapse text-left">
              <thead>
                <tr className="text-2xs font-semibold uppercase tracking-label text-text-muted">
                  <th scope="col" className="px-4 py-3">On</th>
                  <th scope="col" className="px-4 py-3">Mod</th>
                  {flags.map(f => <th key={f.key} scope="col" className="px-3 py-3 text-center" title={f.hint}>{f.label}</th>)}
                  <th scope="col" className="px-4 py-3"><span className="sr-only">Order and remove</span></th>
                </tr>
              </thead>
              <tbody>
                {items.map((item, i) => (
                  <tr key={item.modId} className={`border-t border-line ${item.enabled ? '' : 'opacity-55'}`}>
                    <td className="px-4 py-2">
                      <input type="checkbox" className="size-4" aria-label={`${item.name} enabled`} checked={item.enabled} disabled={!manage}
                        onChange={e => update(i, x => ({ ...x, enabled: e.target.checked }))} />
                    </td>
                    <td className="px-4 py-2">
                      <div className="flex flex-wrap items-center gap-2">
                        {item.workshopId ? <a href={workshopUrl(item.workshopId)} target="_blank" rel="noreferrer" className="font-semibold">{item.name}</a>
                          : <span className="font-semibold">{item.name}</span>}
                        {item.modStatus !== 'Installed' && <StatusBlock tone={modTone[item.modStatus]}>{modStatusText[item.modStatus]}</StatusBlock>}
                        {item.updateAvailable && <StatusBlock tone="warn">Update</StatusBlock>}
                      </div>
                    </td>
                    {flags.map(f => (
                      <td key={f.key} className="px-3 py-2 text-center">
                        <input type="checkbox" className="size-4" aria-label={`${item.name}: ${f.label}`} title={f.hint} checked={item[f.key]} disabled={!manage}
                          onChange={e => update(i, x => setFlag(x, f.key, e.target.checked))} />
                      </td>
                    ))}
                    <td className="px-4 py-2">
                      {manage && (
                        <div className="flex justify-end gap-1">
                          <button type="button" aria-label={`Move ${item.name} up`} disabled={i === 0} onClick={() => move(i, -1)}
                            className="flex size-9 items-center justify-center text-text-muted hover:text-text disabled:opacity-30"><ArrowUpIcon size={16} /></button>
                          <button type="button" aria-label={`Move ${item.name} down`} disabled={i === items.length - 1} onClick={() => move(i, 1)}
                            className="flex size-9 items-center justify-center text-text-muted hover:text-text disabled:opacity-30"><ArrowDownIcon size={16} /></button>
                          <button type="button" aria-label={`Remove ${item.name} from the list`} onClick={() => setItems(xs => xs.filter((_, j) => j !== i))}
                            className="flex size-9 items-center justify-center text-text-muted hover:text-accent-text"><XIcon size={16} /></button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {manage && (
          <div className="flex flex-wrap items-end gap-3 border-t border-line px-6 py-4">
            <SelectField label="Add a mod from the library" value={adding} onChange={e => add(e.target.value)} className="min-w-64">
              <option value="">Choose…</option>
              {available.map(m => <option key={m.id} value={m.id}>{m.name}{m.workshopId ? ` (${m.workshopId})` : ''}</option>)}
            </SelectField>
            {otherLists.length > 0 && (
              <>
                <SelectField label="Copy mods from" value={copyFrom || otherLists[0].id} onChange={e => setCopyFrom(e.target.value)} className="min-w-56">
                  {otherLists.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                </SelectField>
                <Button onClick={() => void copy()}>Copy in</Button>
              </>
            )}
          </div>
        )}
      </Panel>
      {manage && (
        <div className="sticky bottom-0 z-10 flex flex-wrap items-center justify-end gap-3 border border-line bg-surface px-6 py-3">
          {save.error && <p role="alert" className="m-0 mr-auto text-sm text-accent-text">{errorMessage(save.error)}</p>}
          {dirty && <span className="mr-auto text-sm text-text-secondary">Unsaved changes</span>}
          <Button disabled={!dirty} onClick={() => { setName(list.name); setDescription(list.description); setItems(list.items); save.reset() }}>Discard</Button>
          <Button variant="primary" disabled={!dirty} busy={save.isPending} onClick={() => save.mutate()}>Save</Button>
        </div>
      )}
    </div>
  )
}
