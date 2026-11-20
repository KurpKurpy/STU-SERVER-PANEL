import { XIcon } from '@phosphor-icons/react'
import { useEffect, useId, useRef, type ReactNode } from 'react'

// Native <dialog> via showModal(): focus trap, Esc to close, top layer and focus return come from the browser.
export function Dialog({ open, onClose, title, children }: { open: boolean; onClose: () => void; title: string; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()

  useEffect(() => {
    const d = ref.current
    if (!d) return
    if (open && !d.open) d.showModal()
    if (!open && d.open) d.close()
  }, [open])

  return (
    <dialog ref={ref} onClose={onClose} aria-labelledby={titleId}
      className="m-auto w-[min(620px,calc(100%-32px))] border border-line-strong bg-surface p-0 text-text shadow-[0_24px_60px_rgb(0_0_0/0.55)] backdrop:bg-black/75">
      <span aria-hidden="true" className="absolute top-0 left-0 h-[3px] w-14 bg-accent" />
      <header className="flex items-center justify-between gap-4 border-b border-line py-2 pr-2 pl-6">
        <h2 id={titleId} className="display m-0 pt-2 pb-1 text-[26px]">{title}</h2>
        <button type="button" onClick={onClose} aria-label="Close" className="flex size-11 items-center justify-center text-text-muted hover:text-text">
          <XIcon size={20} aria-hidden="true" />
        </button>
      </header>
      <div className="flex flex-col gap-4 px-6 pt-5 pb-6">{open && children}</div>
    </dialog>
  )
}
