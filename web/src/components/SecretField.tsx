import { useId, useState } from 'react'
import { Button } from './Button'
import { StatusBlock } from './StatusBlock'

// A password the panel never shows back: "Set" or "Not set", Replace (write-only) and Clear.
// value: undefined keeps it, '' clears it, any other text replaces it.
// unreadable: the stored value can't be decrypted, so it must be replaced or cleared (the panel refuses saves until all three are).
export function SecretField({ label, set, unreadable, value, onChange, disabled, placeholder = 'New password' }: {
  label: string; set: boolean; unreadable?: boolean; value: string | undefined; onChange: (v: string | undefined) => void; disabled?: boolean; placeholder?: string
}) {
  const id = useId()
  const [editing, setEditing] = useState(false)
  const state = value === '' ? 'Will be cleared' : value !== undefined ? 'Will be replaced' : unreadable ? "Can't be read" : set ? 'Set' : 'Not set'
  return (
    <div className="flex min-w-0 flex-col gap-1.5">
      <span id={id} className="text-2xs font-semibold uppercase tracking-label text-text-muted">{label}</span>
      <div className="flex flex-wrap items-center gap-2">
        <StatusBlock tone={value !== undefined || unreadable ? 'warn' : set ? 'ok' : 'off'}>{state}</StatusBlock>
        {!disabled && !editing && <Button size="sm" onClick={() => setEditing(true)}>Replace</Button>}
        {!disabled && (set || unreadable) && value !== '' && <Button size="sm" variant="danger" onClick={() => { setEditing(false); onChange('') }}>Clear</Button>}
        {value !== undefined && <Button size="sm" onClick={() => { setEditing(false); onChange(undefined) }}>Undo</Button>}
      </div>
      {editing && (
        <input type="password" autoComplete="new-password" aria-labelledby={id} placeholder={placeholder}
          className="min-h-11 w-full border border-line-strong bg-console px-3 text-base text-text"
          onChange={e => onChange(e.target.value === '' ? undefined : e.target.value)} />
      )}
    </div>
  )
}
