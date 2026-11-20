import { useEffect, type ReactNode } from 'react'
import { useAppName } from '../../lib/auth'

// Signed-out screens: an in-game photo with the patch and unit name on one side, the form on the other.
// Below 900px the photo becomes a short band above the form.
export function AuthFrame({ title, children }: { title: string; children: ReactNode }) {
  const appName = useAppName()
  useEffect(() => { document.title = `${title} · ${appName}` }, [title, appName])
  return (
    <div className="grid min-h-dvh md:grid-cols-[minmax(0,1.15fr)_minmax(440px,1fr)]">
      <div className="relative isolate flex min-h-60 flex-col justify-end gap-4 overflow-hidden px-6 pt-10 pb-6 md:min-h-dvh md:px-12 md:pb-12">
        <img src="/brand/breach-smoke-1920.webp" srcSet="/brand/breach-smoke-960.webp 960w, /brand/breach-smoke-1920.webp 1920w"
          sizes="(min-width: 56.25rem) 55vw, 100vw" alt="" className="absolute inset-0 -z-10 size-full object-cover object-[38%_30%]" />
        <div aria-hidden="true" className="absolute inset-0 -z-10 bg-linear-to-t from-bg via-bg/70 to-bg/10 md:bg-linear-to-r md:from-bg/10 md:via-bg/25 md:to-bg" />
        <div aria-hidden="true" className="absolute inset-0 -z-10 bg-linear-to-t from-bg/90 via-transparent to-transparent max-md:hidden" />
        <img src="/brand/patch-256.webp" alt="" width={256} height={269} className="h-16 w-auto self-start md:h-24" />
        <p className="display m-0 text-[44px] md:text-[clamp(56px,6.4vw,96px)]">13th Special<br />Tactics Unit</p>
        <p className="m-0 max-w-sm text-text-secondary">{appName}: the unit's Arma 3 server, missions and roster.</p>
      </div>

      <div className="flex items-start justify-center px-4 py-8 md:items-center md:px-12 md:py-12">
        <main className="stagger flex w-full max-w-[420px] flex-col gap-6">
          <h1 className="display m-0 text-[40px] md:text-[52px]">{title}</h1>
          <section aria-label={title} className="flex flex-col gap-4">{children}</section>
        </main>
      </div>
    </div>
  )
}

export function FormError({ message }: { message: string | null }) {
  return message ? <p role="alert" className="m-0 border border-accent/45 bg-tint px-3 py-2 text-accent-text">{message}</p> : null
}
