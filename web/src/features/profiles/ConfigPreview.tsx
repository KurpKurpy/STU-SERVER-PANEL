import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { Button } from '../../components/Button'
import { TextArea } from '../../components/Field'
import { api, errorMessage } from '../../lib/api'
import { useCan } from '../../lib/auth'
import type { ConfigWarning, SaveResult } from '../../lib/types'
import type { ProfileDraft } from './useProfileDraft'

// The generated file, live. "Edit raw" saves the text as the next revision; settings the form manages become values again,
// everything else is kept as written below them.
export function ConfigPreview({ title, file, text, draft, empty }: {
  title: string; file: 'server.cfg' | 'basic.cfg' | 'difficulty'; text: string | null | undefined; draft: ProfileDraft; empty?: string
}) {
  const can = useCan()
  const queryClient = useQueryClient()
  const [raw, setRaw] = useState<string | null>(null)
  const [rawBase, setRawBase] = useState<number>() // the revision the raw text was opened from
  const [warnings, setWarnings] = useState<ConfigWarning[]>([])
  const save = useMutation({
    mutationFn: (body: string) => api<SaveResult>(`/api/profiles/${draft.detail.data?.id}/files/${file}`, {
      method: 'PUT', json: { baseRevision: rawBase, text: body },
    }),
    onSuccess: async result => {
      setRaw(null)
      setWarnings(result.warnings)
      await Promise.all(['profile', 'profiles', 'servers', 'revisions'].map(k => queryClient.invalidateQueries({ queryKey: [k] })))
    },
  })

  return (
    <section aria-label={title} className="flex min-w-0 flex-col gap-2">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h3 className="m-0 text-sm font-semibold text-text-secondary">{title}</h3>
        {raw === null && can('profiles.manage') && (
          <Button size="sm" disabled={draft.dirty} onClick={() => { setRaw(text ?? ''); setRawBase(draft.detail.data?.revision); save.reset() }}>Edit raw</Button>
        )}
      </div>
      {raw === null ? (
        <>
          {draft.dirty && can('profiles.manage') && <p className="m-0 text-xs text-text-muted">Save or discard your form changes to edit the raw text.</p>}
          <div className="relative max-h-[28rem] overflow-auto border border-line bg-console">
            {text ? <pre className="m-0 p-3 font-mono text-sm leading-relaxed text-text-secondary">{text}</pre>
              : <p className="m-0 p-3 text-sm text-text-muted">{empty ?? 'Nothing is written.'}</p>}
          </div>
          {warnings.length > 0 && (
            <ul className="m-0 flex list-none flex-col gap-1 p-0 text-sm text-warn" aria-label="Kept as written">
              {warnings.map(w => <li key={`${w.line}-${w.message}`}>Line {w.line}: {w.message}</li>)}
            </ul>
          )}
        </>
      ) : (
        <form className="flex flex-col gap-2" onSubmit={e => { e.preventDefault(); save.mutate(raw) }}>
          <TextArea label={`${title} (raw)`} rows={18} spellCheck={false} value={raw} onChange={e => setRaw(e.target.value)}
            error={save.error ? errorMessage(save.error) : undefined}
            hint="Comments and settings the form doesn't know are kept below the managed settings. A password typed here is stored encrypted and not shown again."
            className="[&_textarea]:font-mono [&_textarea]:text-sm" />
          <div className="flex justify-end gap-2.5">
            <Button onClick={() => setRaw(null)}>Cancel</Button>
            <Button type="submit" variant="primary" busy={save.isPending}>Save raw</Button>
          </div>
        </form>
      )}
    </section>
  )
}
