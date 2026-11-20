import { NavLink } from 'react-router'

export function SubNav({ label, items }: { label: string; items: { to: string; label: string; end?: boolean }[] }) {
  return (
    <nav aria-label={label} className="flex flex-wrap gap-1 border-b border-line">
      {items.map(item => (
        <NavLink key={item.to} to={item.to} end={item.end}
          className={({ isActive }) => `-mb-px flex min-h-11 items-center border-b-[3px] px-4 text-[13px] font-bold uppercase tracking-[.1em] no-underline ${isActive ? 'border-accent text-text' : 'border-transparent text-text-muted hover:text-text'}`}>
          {item.label}
        </NavLink>
      ))}
    </nav>
  )
}
