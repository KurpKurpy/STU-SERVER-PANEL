import { BellIcon } from '@phosphor-icons/react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { api } from '../../lib/api'
import type { NotificationFeed, NotificationItem } from '../../lib/types'
import { ago } from '../operations/common'

// Phase 6b Ruling 13: the member's own notifications. Polled every minute and on focus, like the dashboard's own refresh.
// ponytail: polled; push over /hubs/ui if members want them instantly.
export function NotificationBell() {
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const feed = useQuery({ queryKey: ['notifications'], queryFn: () => api<NotificationFeed>('/api/notifications'), refetchInterval: 60_000 })
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    const click = (e: MouseEvent) => { if (box.current && !box.current.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('keydown', key)
    document.addEventListener('mousedown', click)
    return () => { document.removeEventListener('keydown', key); document.removeEventListener('mousedown', click) }
  }, [open])

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['notifications'] })
  async function openItem(n: NotificationItem) {
    setOpen(false)
    if (!n.readAt) { await api(`/api/notifications/${n.id}/read`, { method: 'POST' }); await refresh() }
    if (n.link) navigate(n.link)
  }
  async function readAll() {
    await api('/api/notifications/read-all', { method: 'POST' })
    await refresh()
  }

  const unread = feed.data?.unread ?? 0
  const items = feed.data?.items ?? []
  return (
    // On phones the popover hangs from the title bar (which is `relative`) across the screen; from md up it hangs from the bell.
    <div ref={box} className="md:relative">
      <Button size="sm" aria-expanded={open} aria-haspopup="true" aria-controls="notifications"
        aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'} onClick={() => setOpen(o => !o)}>
        <BellIcon size={16} aria-hidden="true" />
        {unread > 0 && <span aria-hidden="true" className="min-w-5 bg-accent px-1 text-center text-xs font-bold tabular-nums text-black">{unread > 99 ? '99+' : unread}</span>}
      </Button>
      {open && (
        <div id="notifications" role="region" aria-label="Notifications"
          className="absolute inset-x-4 top-full z-20 mt-2 flex max-h-[70vh] flex-col border border-line-strong bg-surface shadow-lg md:inset-x-auto md:right-0 md:w-96">
          <div className="flex items-center justify-between gap-3 border-b border-line px-4 py-3">
            <span className="text-2xs font-semibold uppercase tracking-label text-text-muted">Notifications</span>
            {unread > 0 && <Button size="sm" onClick={readAll}>Mark all read</Button>}
          </div>
          {items.length === 0
            ? <p className="m-0 px-4 py-5 text-text-muted">Nothing yet.</p>
            : (
              <ul className="m-0 list-none overflow-y-auto p-0">
                {items.map(n => (
                  <li key={n.id} className="border-b border-line last:border-b-0">
                    <button type="button" onClick={() => openItem(n)}
                      className={`flex w-full min-h-11 flex-col gap-0.5 px-4 py-2.5 text-left hover:bg-tint ${n.readAt ? 'text-text-muted' : 'text-text'}`}>
                      <span className={`break-words text-sm ${n.readAt ? '' : 'font-semibold'}`}>{n.text}</span>
                      <span className="font-mono text-xs tabular-nums text-text-muted">{ago(n.createdAt)}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
        </div>
      )}
    </div>
  )
}
