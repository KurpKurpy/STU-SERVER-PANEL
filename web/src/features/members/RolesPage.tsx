import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { PlusIcon } from '@phosphor-icons/react'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { Dialog } from '../../components/Dialog'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Check, Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { RoleChip } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { PermissionDto, RoleDto } from '../../lib/types'

const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-2.5 align-top'

export function RolesPage() {
  const manage = useCan()('roles.manage')
  const queryClient = useQueryClient()
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api<RoleDto[]>('/api/roles') })
  const permissions = useQuery({ queryKey: ['permissions'], queryFn: () => api<PermissionDto[]>('/api/permissions'), staleTime: Infinity })
  const [editing, setEditing] = useState<RoleDto | 'new' | null>(null)
  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/roles/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['roles'] }),
  })
  const label = (key: string) => permissions.data?.find(p => p.key === key)?.label ?? key

  return (
    <Panel title="Roles" meta={manage && <Button size="sm" onClick={() => setEditing('new')}><PlusIcon size={14} weight="bold" aria-hidden="true" />New role</Button>}>
      {(roles.isPending || permissions.isPending) && <div className="px-6 py-5"><Loading /></div>}
      {(roles.error || permissions.error) && <div className="px-6 py-5"><ErrorNote error={roles.error ?? permissions.error} /></div>}
      {roles.data && permissions.data && (
        <div className="overflow-x-auto">
          <table className="w-full border-collapse">
            <thead><tr><th className={th}>Role</th><th className={th}>Members</th><th className={th}>Can do</th>{manage && <th className={th}><span className="sr-only">Actions</span></th>}</tr></thead>
            <tbody>
              {roles.data.map(r => (
                <tr key={r.id}>
                  <td className={td}>
                    <span className="font-semibold">{r.name}</span>
                    {r.isSystem && <span className="ml-2 font-mono text-2xs uppercase tracking-label text-text-muted">built-in</span>}
                    {r.description && <p className="m-0 mt-1 text-sm text-text-muted">{r.description}</p>}
                  </td>
                  <td className={`${td} font-mono`}>{r.memberCount}</td>
                  <td className={td}>
                    {r.isOwner
                      ? <span className="text-text-muted">Everything</span>
                      : r.permissions.length === 0
                        ? <span className="text-text-muted">Nothing extra</span>
                        : <span className="flex flex-wrap gap-1.5">{r.permissions.map(p => <RoleChip key={p}>{label(p)}</RoleChip>)}</span>}
                  </td>
                  {manage && (
                    <td className={`${td} whitespace-nowrap text-right`}>
                      {!r.isOwner && <Button size="sm" onClick={() => setEditing(r)}>Edit</Button>}
                      {!r.isSystem && (
                        <Button size="sm" variant="danger" className="ml-2" busy={remove.isPending && remove.variables === r.id}
                          onClick={() => { if (confirm(`Delete the ${r.name} role? Its ${r.memberCount} member(s) lose it.`)) remove.mutate(r.id) }}>Delete</Button>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <Dialog open={editing !== null} onClose={() => setEditing(null)} title={editing === 'new' ? 'New role' : `Edit ${editing?.name ?? ''}`}>
        {editing !== null && permissions.data && <RoleForm current={editing === 'new' ? null : editing} permissions={permissions.data} onDone={() => setEditing(null)} />}
      </Dialog>
    </Panel>
  )
}

function RoleForm({ current, permissions, onDone }: { current: RoleDto | null; permissions: PermissionDto[]; onDone: () => void }) {
  const queryClient = useQueryClient()
  const [error, setError] = useState<string | null>(null)
  const save = useMutation({
    mutationFn: (body: { name: string; description: string; permissions: string[] }) =>
      current ? api(`/api/roles/${current.id}`, { method: 'PUT', json: body }) : api('/api/roles', { json: body }),
    onSuccess: async () => {
      await Promise.all(['roles', 'me', 'member'].map(k => queryClient.invalidateQueries({ queryKey: [k] })))
      onDone()
    },
    onError: err => setError(errorMessage(err)),
  })
  const groups = [...new Set(permissions.map(p => p.group))]

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    save.mutate({ name: String(form.get('name') ?? current?.name ?? ''), description: String(form.get('description') ?? ''), permissions: form.getAll('perm').map(String) })
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <Field label="Name" name="name" defaultValue={current?.name} maxLength={40} required disabled={current?.isSystem}
        hint={current?.isSystem ? "Built-in roles keep their name." : undefined} />
      <Field label="Description" name="description" defaultValue={current?.description ?? ''} maxLength={200} />
      {groups.map(g => (
        <fieldset key={g} className="m-0 flex flex-col border-0 p-0">
          <legend className="mb-1 font-mono text-2xs uppercase tracking-label text-text-muted">{g}</legend>
          {permissions.filter(p => p.group === g).map(p => (
            <Check key={p.key} name="perm" value={p.key} label={p.label} defaultChecked={current?.permissions.includes(p.key)} />
          ))}
        </fieldset>
      ))}
      {error && <p role="alert" className="m-0 text-accent-text">{error}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button type="submit" variant="primary" busy={save.isPending}>{current ? 'Save' : 'Create role'}</Button>
      </div>
    </form>
  )
}
