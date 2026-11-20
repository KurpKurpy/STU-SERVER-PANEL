import type { ButtonHTMLAttributes } from 'react'

export type ButtonVariant = 'default' | 'primary' | 'danger'

// Primary is the one cut-corner button on a screen; the rest stay square.
const variants: Record<ButtonVariant, string> = {
  default: 'border border-line-strong bg-transparent text-text hover:border-text-muted hover:bg-white/[.05]',
  primary: 'chamfer-sm border border-accent bg-accent text-ink hover:border-accent-text hover:bg-accent-text',
  danger: 'border border-accent/55 bg-transparent text-accent-text hover:bg-accent/10',
}

export function buttonClasses(variant: ButtonVariant = 'default', size?: 'sm') {
  const sizing = size === 'sm' ? 'min-h-9 px-3 text-xs pointer-coarse:min-h-11' : 'min-h-11 px-5 text-[13px]'
  return `focus-ring-inset inline-flex items-center justify-center gap-2 whitespace-nowrap font-sans font-bold uppercase tracking-[.1em] no-underline transition-[color,background-color,border-color,transform] duration-150 ease-spring active:scale-[0.98] disabled:cursor-not-allowed disabled:opacity-45 disabled:active:scale-100 ${variants[variant]} ${sizing}`
}

type Props = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: 'sm'; busy?: boolean }

export function Button({ variant = 'default', size, busy, type = 'button', className = '', disabled, children, ...rest }: Props) {
  return (
    <button {...rest} type={type} disabled={disabled || busy} aria-busy={busy || undefined} className={`${buttonClasses(variant, size)} ${className}`}>
      {busy ? 'Working…' : children}
    </button>
  )
}
