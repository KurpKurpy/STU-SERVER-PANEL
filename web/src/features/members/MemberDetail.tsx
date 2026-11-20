import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { CheckIcon, XIcon } from '@phosphor-icons/react'
import { useState, type FormEvent } from 'react'
import { Button } from '../../components/Button'
import { CopyField } from '../../components/CopyField'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { Check, Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { RoleChip, StatusBlock } from '../../components/StatusBlock'
import { ApiError, api, errorMessage } from '../../lib/api'
import { useCan, useMe } from '../../lib/auth'
import type { MemberDetailDto, MemberPatch, PermissionDto, ResetLink, RoleDto } from '../../lib/types'

const lastOwner = 'The unit needs at least one Owner.'

export function MemberDetail({ id }: { id: string }) {
  const can = useCan()
  const { data: me } = useMe()
  const manage = can('members.manage')
  const queryClient = useQueryClient()
  // No focus refetch and a form key bumped only on save: alt-tabbing away must never wipe unsaved edits.
  const detail = useQuery({ queryKey: ['member', id], queryFn: () => api<MemberDetailDto>(`/api/members/${id}`), refetchOnWindowFocus: false })
  const [formVersion, setFormVersion] = useState(0)
  const roles = useQuery({ queryKey: ['roles'], queryFn: () => api<RoleDto[]>('/api/roles') })
  const permissions = useQuery({ queryKey: ['permissions'], queryFn: () => api<PermissionDto[]>('/api/permissions'), staleTime: Infinity })
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [reset, setReset] = useState<ResetLink | null>(null)

  const patch = useMutation({
    mutationFn: (body: MemberPatch) => api<MemberDetailDto>(`/api/members/${id}`, { method: 'PATCH', json: body }),
    onSuccess: data => {
      queryClient.setQueryData(['member', id], data)
      setFormVersion(v => v + 1)
      void queryClient.invalidateQueries({ queryKey: ['members'] })
      void queryClient.invalidateQueries({ queryKey: ['me'] })
      setMessage({ ok: true, text: 'Saved.' })
    },
    onError: err => setMessage({ ok: false, text: err instanceof ApiError && err.code === 'last_owner' ? lastOwner : errorMessage(err) }),
  })
  const resetLink = useMutation({
    mutationFn: () => api<ResetLink>(`/api/members/${id}/reset-link`, { method: 'POST' }),
    onSuccess: setReset,
    onError: err => setMessage({ ok: false, text: errorMessage(err) }),
  })

  if (detail.isPending || roles.isPending || permissions.isPending) return <Panel title="Member"><div className="px-6 py-5"><Loading /></div></Panel>
  const error = detail.error ?? roles.error ?? permissions.error
  if (error || !detail.data || !roles.data || !permissions.data) return <Panel title="Member"><div className="px-6 py-5"><ErrorNote error={error} /></div></Panel>

  const { member, grants, denies, effective } = detail.data

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    const choice = (key: string) => String(form.get(`perm:${key}`) ?? 'inherit')
    setMessage(null)
    patch.mutate({
      displayName: String(form.get('displayName') ?? ''),
      rank: String(form.get('rank') ?? ''),
      section: String(form.get('section') ?? ''),
      roles: form.getAll('role').map(String),
      grants: permissions.data!.map(p => p.key).filter(k => choice(k) === 'grant'),
      denies: permissions.data!.map(p => p.key).filter(k => choice(k) === 'deny'),
    })
  }

  function toggleDisabled() {
    const verb = member.disabled ? 'Enable' : 'Disable'
    if (confirm(`${verb} ${member.userName}?${member.disabled ? '' : ' They will be signed out within a minute.'}`)) patch.mutate({ disabled: !member.disabled })
  }

  const title = [member.rank, member.userName].filter(Boolean).join(' ')
  return (
    <Panel title={title} meta={member.section ?? undefined}>
      <div className="flex flex-col gap-5 px-6 pt-5 pb-6">
        <div className="flex flex-wrap items-center gap-2">
          {member.disabled && <StatusBlock tone="bad">Disabled</StatusBlock>}
          {member.twoFactorEnabled ? <StatusBlock tone="ok">2FA on</StatusBlock> : <StatusBlock tone="off">2FA off</StatusBlock>}
          <span className="font-mono text-xs uppercase tracking-label text-text-muted">Last seen {formatWhen(member.lastLoginAt)}</span>
        </div>

        {manage ? (
          <form key={formVersion} onSubmit={submit} className="flex flex-col gap-5">
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-3">
              <Field label="Display name" name="displayName" defaultValue={member.displayName} maxLength={64} required className="sm:col-span-3" />
              <Field label="Rank" name="rank" defaultValue={member.rank ?? ''} maxLength={32} placeholder="e.g. GYSGT" />
              <Field label="Section" name="section" defaultValue={member.section ?? ''} maxLength={32} placeholder="e.g. 13:M2" className="sm:col-span-2" />
            </div>
            <fieldset className="m-0 flex flex-col border-0 p-0">
              <legend className="mb-1 font-mono text-2xs uppercase tracking-label text-text-muted">Roles</legend>
              {roles.data.map(r => (
                <Check key={r.id} name="role" value={r.name} label={r.name} defaultChecked={member.roles.includes(r.name)}
                  disabled={r.isOwner && !me?.isOwner} hint={r.isOwner && !me?.isOwner ? 'Only Owners can change this.' : undefined} />
              ))}
            </fieldset>
            <fieldset className="m-0 flex flex-col gap-2 border-0 p-0">
              <legend className="mb-1 font-mono text-2xs uppercase tracking-label text-text-muted">Just for {member.userName}</legend>
              {permissions.data.map(p => (
                <label key={p.key} className="grid grid-cols-[minmax(0,1fr)_120px] items-center gap-3">
                  <span className="text-sm">{p.label}</span>
                  <select name={`perm:${p.key}`} aria-label={`${p.label}: from roles, grant or deny`} defaultValue={grants.includes(p.key) ? 'grant' : denies.includes(p.key) ? 'deny' : 'inherit'}
                    className="min-h-10 border border-line-strong bg-black/35 px-2 text-sm pointer-coarse:min-h-11">
                    <option value="inherit">From roles</option>
                    <option value="grant">Grant</option>
                    <option value="deny">Deny</option>
                  </select>
                </label>
              ))}
            </fieldset>
            {message && <p role={message.ok ? 'status' : 'alert'} className={`m-0 ${message.ok ? 'text-ok' : 'text-accent-text'}`}>{message.text}</p>}
            <Button type="submit" variant="primary" busy={patch.isPending} className="self-start">Save changes</Button>
          </form>
        ) : (
          <div className="flex flex-wrap gap-1.5">{member.roles.map(r => <RoleChip key={r}>{r}</RoleChip>)}</div>
        )}

        <div className="flex flex-col gap-1.5 border-t border-line pt-4">
          <span className="font-mono text-2xs uppercase tracking-label text-text-muted">Can do</span>
          {permissions.data.map(p => {
            const has = effective.includes(p.key)
            return (
              <span key={p.key} className={`flex items-center gap-2.5 text-sm ${has ? '' : 'text-text-muted'}`}>
                {has ? <CheckIcon size={15} weight="bold" className="text-ok" aria-label="Yes" /> : <XIcon size={15} className="text-text-subtle" aria-label="No" />}{p.label}
              </span>
            )
          })}
        </div>

        {manage && (
          <div className="flex flex-col gap-3 border-t border-line pt-4">
            <div className="flex flex-wrap gap-2">
              <Button size="sm" busy={resetLink.isPending} onClick={() => resetLink.mutate()}>Reset-password link</Button>
              <Button size="sm" variant="danger" busy={patch.isPending} onClick={toggleDisabled}>{member.disabled ? 'Enable account' : 'Disable account'}</Button>
            </div>
            {reset && (
              <>
                <CopyField label="Reset link" value={reset.url} />
                <span className="text-xs text-text-muted">Send it to {member.userName} directly. It works once and expires {formatWhen(reset.expiresAt)}.</span>
              </>
            )}
          </div>
        )}
      </div>
    </Panel>
  )
}
