import { useQuery } from '@tanstack/react-query'
import { Link, Outlet, useSearchParams } from 'react-router'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { RoleChip, StatusBlock } from '../../components/StatusBlock'
import { SubNav } from '../../components/SubNav'
import { api } from '../../lib/api'
import { RequirePermission, useCan } from '../../lib/auth'
import type { MemberDto } from '../../lib/types'
import { MemberDetail } from './MemberDetail'

export function MembersLayout() {
  const can = useCan()
  const tabs = [
    { to: '/members', label: 'Members', end: true, perm: 'members.view' },
    { to: '/members/roles', label: 'Roles', perm: 'members.view' },
    { to: '/members/invites', label: 'Invites', perm: 'members.manage' },
    { to: '/members/audit', label: 'Audit log', perm: 'audit.view' },
  ].filter(t => can(t.perm))
  return (
    <RequirePermission perm="members.view">
      <SubNav label="Member sections" items={tabs} />
      <Outlet />
    </RequirePermission>
  )
}

const th = 'whitespace-nowrap border-b border-line px-4 py-3 text-left text-2xs font-semibold uppercase tracking-label text-text-muted'
const td = 'border-b border-line px-4 py-2.5'

export function MembersPage() {
  const [params] = useSearchParams()
  const selected = params.get('m')
  const members = useQuery({ queryKey: ['members'], queryFn: () => api<MemberDto[]>('/api/members') })
  const count = members.data?.length ?? 0

  return (
    <div className="stagger grid grid-cols-1 items-start gap-5 lg:grid-cols-12">
      <Panel title="Roster" meta={members.data && `${count} account${count === 1 ? '' : 's'}`} className="lg:col-span-7">
        {members.isPending && <div className="px-6 py-5"><Loading /></div>}
        {members.error && <div className="px-6 py-5"><ErrorNote error={members.error} onRetry={() => members.refetch()} /></div>}
        {members.data && (
          <div className="overflow-x-auto">
            <table className="w-full border-collapse">
              <thead>
                <tr><th className={th}>Rank</th><th className={th}>Callsign</th><th className={th}>Section</th><th className={th}>Panel roles</th><th className={th}>2FA</th><th className={th}>Last seen</th></tr>
              </thead>
              <tbody>
                {members.data.map(m => (
                  <tr key={m.id} className={m.id === selected ? 'bg-white/6' : 'hover:bg-white/[.03]'}>
                    <td className={`${td} font-mono text-text-muted`}>{m.rank ?? '—'}</td>
                    <td className={td}>
                      <Link to={`/members?m=${m.id}`} aria-current={m.id === selected ? 'true' : undefined} className="font-semibold text-text no-underline hover:text-accent-text">{m.userName}</Link>
                      {m.disabled && <span className="ml-2"><StatusBlock tone="bad">Disabled</StatusBlock></span>}
                    </td>
                    <td className={`${td} font-mono text-text-muted`}>{m.section ?? '—'}</td>
                    <td className={td}><span className="flex flex-wrap gap-1.5">{m.roles.map(r => <RoleChip key={r}>{r}</RoleChip>)}</span></td>
                    <td className={td}>{m.twoFactorEnabled ? <StatusBlock tone="ok">On</StatusBlock> : <StatusBlock tone="off">Off</StatusBlock>}</td>
                    <td className={`${td} whitespace-nowrap font-mono text-text-muted`}>{formatWhen(m.lastLoginAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Panel>
      <div className="lg:col-span-5">
        {selected
          ? <MemberDetail key={selected} id={selected} />
          : <Panel title="Member"><p className="m-0 px-6 py-5 text-text-muted">Pick a callsign to see their roles and access.</p></Panel>}
      </div>
    </div>
  )
}
