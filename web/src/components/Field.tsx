import { useId, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react'

const control = 'min-h-11 w-full border border-line-strong bg-console px-3 text-base text-text placeholder:text-text-subtle hover:border-text-subtle focus-visible:border-accent-text aria-[invalid=true]:border-bad'

function Label({ id, children }: { id: string; children: ReactNode }) {
  return <label htmlFor={id} className="text-2xs font-semibold uppercase tracking-label text-text-muted">{children}</label>
}

function Help({ id, error, hint }: { id: string; error?: string; hint?: ReactNode }) {
  if (error) return <p id={`${id}-err`} role="alert" className="m-0 text-sm text-accent-text">{error}</p>
  return hint ? <p id={`${id}-hint`} className="m-0 text-xs text-text-muted">{hint}</p> : null
}

type FieldProps = InputHTMLAttributes<HTMLInputElement> & { label: string; error?: string; hint?: ReactNode }

export function Field({ label, error, hint, className = '', ...input }: FieldProps) {
  const id = useId()
  return (
    <div className={`flex min-w-0 flex-col gap-1.5 ${className}`}>
      <Label id={id}>{label}</Label>
      <input id={id} aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-err` : hint ? `${id}-hint` : undefined} {...input} className={control} />
      <Help id={id} error={error} hint={hint} />
    </div>
  )
}

type AreaProps = TextareaHTMLAttributes<HTMLTextAreaElement> & { label: string; error?: string; hint?: ReactNode }

export function TextArea({ label, error, hint, className = '', ...area }: AreaProps) {
  const id = useId()
  return (
    <div className={`flex min-w-0 flex-col gap-1.5 ${className}`}>
      <Label id={id}>{label}</Label>
      <textarea id={id} aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-err` : hint ? `${id}-hint` : undefined} {...area} className={`${control} min-h-28 py-2.5`} />
      <Help id={id} error={error} hint={hint} />
    </div>
  )
}

type SelectProps = SelectHTMLAttributes<HTMLSelectElement> & { label: string; hint?: ReactNode }

export function SelectField({ label, hint, className = '', children, ...select }: SelectProps) {
  const id = useId()
  return (
    <div className={`flex min-w-0 flex-col gap-1.5 ${className}`}>
      <Label id={id}>{label}</Label>
      <select id={id} aria-describedby={hint ? `${id}-hint` : undefined} {...select} className={control}>{children}</select>
      <Help id={id} hint={hint} />
    </div>
  )
}

export function Check({ label, hint, ...input }: InputHTMLAttributes<HTMLInputElement> & { label: ReactNode; hint?: ReactNode }) {
  return (
    <label className="flex min-h-11 cursor-pointer items-center gap-3">
      <input type="checkbox" {...input} className="size-5 accent-[var(--color-accent)]" />
      <span className="flex flex-col"><span>{label}</span>{hint && <span className="text-xs text-text-muted">{hint}</span>}</span>
    </label>
  )
}
