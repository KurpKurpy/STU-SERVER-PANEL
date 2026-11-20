import { ArchiveIcon, CalendarDotsIcon, ClockClockwiseIcon, FolderOpenIcon, GearSixIcon, HardDrivesIcon, ListChecksIcon, MapTrifoldIcon, PuzzlePieceIcon, SlidersHorizontalIcon, SquaresFourIcon, UsersIcon, type Icon } from '@phosphor-icons/react'
import { Link, NavLink } from 'react-router'
import { useAppName, useCan } from '../lib/auth'

interface NavItem { to: string; label: string; icon: Icon; perm?: string; end?: boolean }

// Only sections that work in the current phase are listed. Later phases add Operations, Missions and so on here.
const groups: { label: string; items: NavItem[] }[] = [
  { label: 'Operations', items: [
    { to: '/', label: 'Dashboard', icon: SquaresFourIcon, end: true },
    { to: '/operations', label: 'Operations', icon: CalendarDotsIcon, perm: 'operations.view' },
    { to: '/server', label: 'Server', icon: HardDrivesIcon, perm: 'server.view' },
    { to: '/profiles', label: 'Profiles', icon: SlidersHorizontalIcon, perm: 'profiles.view' },
    { to: '/missions', label: 'Missions', icon: MapTrifoldIcon, perm: 'missions.view' },
    { to: '/mods', label: 'Mods', icon: PuzzlePieceIcon, perm: 'mods.view', end: true },
    { to: '/mods/lists', label: 'Mod lists', icon: ListChecksIcon, perm: 'mods.view' },
    { to: '/files', label: 'Files', icon: FolderOpenIcon, perm: 'files.*' },
    { to: '/backups', label: 'Backups', icon: ArchiveIcon, perm: 'backups.manage' },
    { to: '/schedules', label: 'Schedules', icon: ClockClockwiseIcon, perm: 'schedules.manage' },
  ] },
  { label: 'Unit', items: [
    { to: '/members', label: 'Members', icon: UsersIcon, perm: 'members.view' },
    { to: '/settings', label: 'Settings', icon: GearSixIcon },
  ] },
]

// The active item is a solid red arrow with black text, like a highlighted row on the order-of-battle sheet.
const arrow = '[clip-path:polygon(0_0,calc(100%-12px)_0,100%_50%,calc(100%-12px)_100%,0_100%)]'

export function Sidebar({ open }: { open: boolean }) {
  const can = useCan()
  const appName = useAppName()

  return (
    <aside className="flex flex-col gap-6 border-line bg-sidebar px-3 py-4 md:sticky md:top-0 md:h-dvh md:w-60 md:shrink-0 md:border-r md:py-6">
      <Link to="/" aria-label={`13th Special Tactics Unit, ${appName} home`} className="flex items-center gap-3 px-1.5 text-text no-underline">
        <img src="/brand/patch-256.webp" alt="" width={256} height={269} className="h-14 w-auto shrink-0" />
        <span className="flex flex-col gap-1.5">
          <span className="display text-[21px]">13th Special<br />Tactics Unit</span>
          <span className="text-xs font-medium text-text-muted">{appName}</span>
        </span>
      </Link>

      <nav id="main-nav" aria-label="Main" className={`${open ? 'flex' : 'hidden'} flex-col gap-5 md:flex`}>
        {groups.map(group => {
          const items = group.items.filter(i => !i.perm || can(i.perm))
          if (items.length === 0) return null
          return (
            <div key={group.label} className="flex flex-col gap-0.5">
              <span className="px-3 pb-1.5 text-2xs font-semibold uppercase tracking-label text-text-subtle">{group.label}</span>
              {items.map(item => (
                <NavLink key={item.to} to={item.to} end={item.end}
                  className={({ isActive }) => `focus-ring-inset flex min-h-11 items-center gap-3 pr-4 pl-3 text-[15px] no-underline ${arrow} ${isActive ? 'bg-accent font-bold text-ink' : 'font-medium text-text-secondary hover:bg-white/[.06] hover:text-text'}`}>
                  {({ isActive }) => <><item.icon size={20} weight={isActive ? 'bold' : 'regular'} aria-hidden="true" />{item.label}</>}
                </NavLink>
              ))}
            </div>
          )
        })}
      </nav>
    </aside>
  )
}
