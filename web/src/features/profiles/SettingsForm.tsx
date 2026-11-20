import { useId, useState } from 'react'
import { Button } from '../../components/Button'
import { Check, Field, SelectField, TextArea } from '../../components/Field'
import { Loading } from '../../components/Feedback'
import type { CfgKey, ConfigFile, JsonValue } from '../../lib/types'
import { useCatalog } from './useProfileDraft'

interface Props {
  file: ConfigFile
  tab: string
  values: Record<string, JsonValue>
  onChange: (key: string, value: JsonValue | undefined) => void
  errors: Record<string, string>
  readOnly?: boolean
  rawClasses?: string[] // classes kept as raw text in the extras: their fields can't be edited here
}

// Renders the catalog's keys for one file and tab, grouped. A key left at "Default" isn't written, so Arma's own default applies.
export function SettingsForm({ file, tab, values, onChange, errors, readOnly, rawClasses = [] }: Props) {
  const catalog = useCatalog()
  if (catalog.isPending) return <Loading />
  const keys = (catalog.data ?? []).filter(k => k.file === file && k.tab === tab && !k.secret)
  const groups = [...new Set(keys.map(k => k.group))]
  return (
    <div className="flex flex-col gap-6">
      {groups.map(group => (
        <fieldset key={group} className="m-0 flex min-w-0 flex-col gap-4 border-0 p-0">
          <legend className="mb-3 text-sm font-semibold text-text-secondary">{group}</legend>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
            {keys.filter(k => k.group === group).map(k => (
              <KeyField key={k.name} cfg={k} value={values[k.name]} error={errors[`${file}.${k.name}`]}
                raw={k.class !== null && rawClasses.includes(k.class)} readOnly={readOnly}
                onChange={v => onChange(k.name, v)} />
            ))}
          </div>
        </fieldset>
      ))}
    </div>
  )
}

function placeholder(k: CfgKey) {
  return k.default === null ? 'Default' : k.default === '' ? 'Default (empty)' : `Default (${k.default})`
}

function hintFor(k: CfgKey, raw: boolean) {
  if (raw) return `Set in the raw editor: class ${k.class} has entries this form doesn't know.`
  const range = k.min !== null && k.max !== null ? `${k.min}–${k.max}.` : k.min !== null ? `At least ${k.min}.` : k.max !== null ? `At most ${k.max}.` : ''
  return [k.help, range].filter(Boolean).join(' ') || undefined
}

function KeyField({ cfg: k, value, error, raw, readOnly, onChange }: {
  cfg: CfgKey; value: JsonValue | undefined; error?: string; raw: boolean; readOnly?: boolean; onChange: (v: JsonValue | undefined) => void
}) {
  const disabled = raw || readOnly
  const hint = hintFor(k, raw)
  const label = k.file === 'launch' ? `${k.label} (${k.name})` : k.label

  if (k.type === 'Flag')
    return <Check label={label} hint={hint} checked={value === true} disabled={disabled} onChange={e => onChange(e.target.checked ? true : undefined)} />

  if (k.type === 'Bool01' || k.type === 'BoolWord')
    return (
      <SelectField label={label} hint={error ?? hint} disabled={disabled}
        value={value === undefined ? '' : value ? 'on' : 'off'} onChange={e => onChange(e.target.value === '' ? undefined : e.target.value === 'on')}>
        <option value="">{k.default === null ? 'Default' : `Default (${['1', 'true'].includes(k.default) ? 'on' : 'off'})`}</option>
        <option value="on">On</option>
        <option value="off">Off</option>
      </SelectField>
    )

  if (k.choices)
    return (
      <SelectField label={label} hint={error ?? hint} disabled={disabled} value={value === undefined ? '' : String(value)}
        onChange={e => onChange(e.target.value === '' ? undefined : k.type === 'Text' ? e.target.value : Number(e.target.value))}>
        <option value="">{placeholder(k)}</option>
        {k.choices.map(c => <option key={c} value={c}>{c}</option>)}
      </SelectField>
    )

  if (k.type === 'Integer' || k.type === 'Number')
    return (
      <Field label={label} type="number" inputMode="decimal" step={k.type === 'Integer' ? 1 : 'any'} min={k.min ?? undefined} max={k.max ?? undefined}
        placeholder={placeholder(k)} hint={hint} error={error} disabled={disabled}
        value={value === undefined || value === null ? '' : String(value)}
        onChange={e => onChange(e.target.value === '' ? undefined : Number(e.target.value))} />
    )

  if (k.type === 'TextList') return <ListField cfg={k} value={value as string[] | undefined} error={error} hint={hint} disabled={disabled} onChange={onChange} label={label} />
  if (k.type === 'NumberList') return <NumberListField cfg={k} value={value as number[] | undefined} error={error} hint={hint} disabled={disabled} onChange={onChange} label={label} />

  return (
    <Field label={label} placeholder={placeholder(k)} hint={hint} error={error} disabled={disabled} className={k.file === 'launch' ? '[&_input]:font-mono' : ''}
      value={typeof value === 'string' ? value : ''} onChange={e => onChange(e.target.value === '' ? undefined : e.target.value)} />
  )
}

// A list textarea's text as typed (a fresh Enter makes no item yet, so it must not be re-joined away), following the list
// when it changes from outside: discard, back to default, a reload.
export function useListText(value: string[] | undefined, parse: (text: string) => string[]) {
  const joined = value?.join('\n') ?? ''
  const [text, setText] = useState(joined)
  const synced = value === undefined ? text === '' : JSON.stringify(parse(text)) === JSON.stringify(value)
  if (!synced && text !== joined) setText(joined)
  return [synced ? text : joined, setText] as const
}

// One item per line; blank lines are kept as "" items (MOTD pauses), and a trailing newline adds none.
const toLines = (t: string) => {
  if (t === '') return []
  const lines = t.split('\n')
  if (t.endsWith('\n')) lines.pop()
  return lines
}

function ListField({ cfg, value, error, hint, disabled, label, onChange }: {
  cfg: CfgKey; value?: string[]; error?: string; hint?: string; disabled?: boolean; label: string; onChange: (v: JsonValue | undefined) => void
}) {
  const [text, setText] = useListText(value, toLines)
  return (
    <div className="flex flex-col gap-2 md:col-span-2">
      <TextArea label={label} rows={3} placeholder="Not set" disabled={disabled} error={error}
        hint={value !== undefined && value.length === 0 ? `Set to an empty list. ${cfg.help ?? ''}` : hint}
        value={text} className="[&_textarea]:font-mono [&_textarea]:text-sm"
        onChange={e => { setText(e.target.value); onChange(toLines(e.target.value)) }} />
      {value !== undefined && !disabled && <Button size="sm" className="self-start" onClick={() => onChange(undefined)}>Back to default</Button>}
    </div>
  )
}

function NumberListField({ cfg, value, error, hint, disabled, label, onChange }: {
  cfg: CfgKey; value?: number[]; error?: string; hint?: string; disabled?: boolean; label: string; onChange: (v: JsonValue | undefined) => void
}) {
  const id = useId()
  const count = cfg.length ?? Math.max(1, value?.length ?? 1)
  const items = Array.from({ length: count }, (_, i) => value?.[i])
  function set(i: number, raw: string) {
    const next = items.map((v, j) => (j === i ? (raw === '' ? undefined : Number(raw)) : v))
    onChange(next.every(v => v === undefined) ? undefined : next.map(v => v ?? 0))
  }
  return (
    <fieldset className="m-0 flex min-w-0 flex-col gap-1.5 border-0 p-0" aria-describedby={`${id}-help`}>
      <legend className="text-2xs font-semibold uppercase tracking-label text-text-muted">{label}</legend>
      <div className="flex flex-wrap gap-2">
        {items.map((v, i) => (
          <input key={i} type="number" aria-label={`${label}, value ${i + 1}`} disabled={disabled} value={v ?? ''} placeholder="Default"
            onChange={e => set(i, e.target.value)}
            className="min-h-11 w-28 border border-line-strong bg-console px-3 text-base text-text placeholder:text-text-subtle" />
        ))}
      </div>
      <p id={`${id}-help`} role={error ? 'alert' : undefined} className={`m-0 text-xs ${error ? 'text-accent-text' : 'text-text-muted'}`}>
        {error ?? hint ?? `Default ${cfg.default ?? ''}`}
      </p>
    </fieldset>
  )
}
