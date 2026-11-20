import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { CopyField } from '../../components/CopyField'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { SelectField } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { RoleChip } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useMe } from '../../lib/auth'
import type { InviteCreated, InviteDto, RoleDto } from '../../lib/types'

const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-2.5'

export function InvitesPage() {
  const { data: me } = useMe()
  const queryClient = useQueryClient()
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api<RoleDto[]>('/api/roles') })
  const pending = useQuery({ queryKey: ['invites'], queryFn: () => api<InviteDto[]>('/api/invites') })
  const [created, setCreated] = useState<InviteCreated | null>(null)
  const [error, setError] = useState<string | null>(null)

  const create = useMutation({
    mutationFn: (body: { roleId: string; expiresHours: number }) => api<InviteCreated>('/api/invites', { json: body }),
    onSuccess: inv => { setCreated(inv); setError(null); void queryClient.invalidateQueries({ queryKey: ['invites'] }) },
    onError: err => setError(errorMessage(err)),
  })
  const revoke = useMutation({
    mutationFn: (id: string) => api(`/api/invites/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['invites'] }),
  })

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    create.mutate({ roleId: String(form.get('roleId')), expiresHours: Number(form.get('expiresHours')) })
  }

  const choosable = roles.data?.filter(r => !r.isOwner || me?.isOwner) ?? []
  return (
    <div className="stagger grid grid-cols-1 items-start gap-5 lg:grid-cols-12">
      <Panel title="New invite" className="lg:col-span-5">
        <div className="flex flex-col gap-4 px-6 pt-5 pb-6">
          {roles.isPending && <Loading />}
          {roles.error && <ErrorNote error={roles.error} />}
          {roles.data && (
            <form onSubmit={submit} className="flex flex-col gap-4">
              <SelectField label="Joins as" name="roleId" defaultValue={choosable.find(r => r.name === 'Member')?.id}>
                {choosable.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}
              </SelectField>
              <SelectField label="Link expires after" name="expiresHours" defaultValue="72">
                <option value="24">24 hours</option>
                <option value="72">3 days</option>
                <option value="168">7 days</option>
              </SelectField>
              {error && <p role="alert" className="m-0 text-accent-text">{error}</p>}
              <Button type="submit" variant="primary" busy={create.isPending} className="self-start">Create invite link</Button>
            </form>
          )}
          {created && (
            <div className="flex flex-col gap-2 border-t border-line pt-4">
              <CopyField label="Invite link" value={created.url} />
              <span className="text-xs text-text-muted">Paste it into a Discord DM. No email is sent. Works once, until {formatWhen(created.expiresAt)}.</span>
            </div>
          )}
        </div>
      </Panel>
      <Panel title="Pending invites" meta={pending.data && `${pending.data.length} open`} className="lg:col-span-7">
        {pending.isPending && <div className="px-6 py-5"><Loading /></div>}
        {pending.error && <div className="px-6 py-5"><ErrorNote error={pending.error} onRetry={() => pending.refetch()} /></div>}
        {pending.data?.length === 0 && <p className="m-0 px-6 py-5 text-text-muted">No open invites.</p>}
        {!!pending.data?.length && (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead><tr><th className={th}>Role</th><th className={th}>Created by</th><th className={th}>Created</th><th className={th}>Expires</th><th className={th}><span className="sr-only">Actions</span></th></tr></thead>
              <tbody>
                {pending.data.map(i => (
                  <tr key={i.id}>
                    <td className={td}><RoleChip>{i.roleName}</RoleChip></td>
                    <td className={td}>{i.createdBy}</td>
                    <td className={`${td} font-mono text-text-muted`}>{formatWhen(i.createdAt)}</td>
                    <td className={`${td} font-mono text-text-muted`}>{formatWhen(i.expiresAt)}</td>
                    <td className={`${td} text-right`}>
                      <Button size="sm" variant="danger" busy={revoke.isPending && revoke.variables === i.id}
                        onClick={() => { if (confirm('Cancel this invite? The link stops working.')) revoke.mutate(i.id) }}>Revoke</Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
    </div>
  )
}
