import type { ReactNode } from 'react'

// Square card with a stencil title. A short red bar marks the top-left corner, the way the order-of-battle
// sheets head each block in red. `emphasis` turns the card red-tinted for anything that needs action now.
export function Panel({ title, meta, children, className = '', emphasis = false }: {
  title: string
  meta?: ReactNode
  children: ReactNode
  className?: string
  emphasis?: boolean
}) {
  return (
    <section aria-label={title}
      className={`relative flex min-w-0 flex-col border ${emphasis ? 'border-accent/45 bg-tint' : 'border-line bg-surface'} ${className}`}>
      <span aria-hidden="true" className={`absolute -top-px -left-px h-[3px] bg-accent ${emphasis ? 'right-[-1px]' : 'w-14'}`} />
      <header className="flex flex-wrap items-end justify-between gap-x-4 gap-y-2 border-b border-line px-6 pt-5 pb-4">
        <h2 className="display m-0 text-[26px] text-text">{title}</h2>
        {meta && <div className="flex items-center gap-2 text-sm text-text-muted">{meta}</div>}
      </header>
      {children}
    </section>
  )
}
