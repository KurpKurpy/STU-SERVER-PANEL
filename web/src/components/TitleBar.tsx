import { ListIcon, SignOutIcon } from '@phosphor-icons/react'
import { useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router'
import { api } from '../lib/api'
import { useMe } from '../lib/auth'
import { stopHub } from '../lib/hub'
import { NotificationBell } from '../features/notifications/NotificationBell'
import { Button } from './Button'

export function TitleBar({ title, menuOpen, onMenu }: { title: string; menuOpen: boolean; onMenu: () => void }) {
  const { data: me } = useMe()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const role = me ? (me.isOwner ? 'Owner' : me.roles[0] ?? 'Member') : ''

  async function signOut() {
    await api('/api/auth/logout', { method: 'POST' })
    stopHub()
    queryClient.clear()
    navigate('/login', { replace: true })
  }

  return (
    <header className="relative flex flex-wrap items-center gap-x-5 gap-y-3 border-b border-line px-6 py-4 md:px-8 md:py-5">
      <Button size="sm" className="md:hidden" aria-expanded={menuOpen} aria-controls="main-nav" onClick={onMenu}>
        <ListIcon size={16} aria-hidden="true" />Menu
      </Button>
      <h1 className="display order-last m-0 w-full min-w-0 text-[34px] md:order-none md:w-auto md:flex-1 md:text-[42px]">{title}</h1>
      {me && (
        <span className="flex flex-col items-end leading-tight max-md:hidden">
          <span className="font-semibold">{me.userName}</span>
          <span className="text-xs text-text-muted">{role}</span>
        </span>
      )}
      <div className="ml-auto flex items-center gap-2 md:ml-0">
        {me && <NotificationBell />}
        <Button size="sm" onClick={signOut}><SignOutIcon size={15} aria-hidden="true" />Sign out</Button>
      </div>
    </header>
  )
}
