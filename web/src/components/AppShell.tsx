import { useEffect, useState } from 'react'
import { Outlet, useLocation, useMatches } from 'react-router'
import { useAppName, useMe } from '../lib/auth'
import { hubUser } from '../lib/hub'
import { Sidebar } from './Sidebar'
import { TitleBar } from './TitleBar'

export function AppShell() {
  const [menuOpen, setMenuOpen] = useState(false)
  const { pathname } = useLocation()
  const appName = useAppName()
  const matches = useMatches()
  const { data: me } = useMe()
  const title = [...matches].reverse().map(m => (m.handle as { title?: string } | undefined)?.title).find(Boolean) ?? 'Console'

  useEffect(() => setMenuOpen(false), [pathname])
  useEffect(() => hubUser(me?.id), [me?.id])
  useEffect(() => { document.title = `${title} · ${appName}` }, [title, appName])

  return (
    <div className="flex min-h-dvh flex-col md:flex-row">
      <a href="#main" className="sr-only z-60 bg-accent px-4 py-2 text-ink focus:not-sr-only focus:fixed focus:top-2 focus:left-2">Skip to content</a>
      <Sidebar open={menuOpen} />
      <div className="flex min-w-0 flex-1 flex-col">
        <TitleBar title={title} menuOpen={menuOpen} onMenu={() => setMenuOpen(o => !o)} />
        <main id="main" tabIndex={-1} className="stagger flex w-full max-w-[1400px] flex-col gap-6 px-4 pt-6 pb-10 outline-none sm:px-6 md:px-8">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
