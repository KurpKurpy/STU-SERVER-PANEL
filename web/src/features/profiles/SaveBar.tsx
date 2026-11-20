import { useState } from 'react'
import { Button } from '../../components/Button'
import type { ProfileDraft } from './useProfileDraft'

// Sticky at the foot of the profile while there are unsaved changes; Save makes the next revision.
export function SaveBar({ draft }: { draft: ProfileDraft }) {
  const [note, setNote] = useState('')
  if (!draft.dirty && !draft.saveError) return null
  const next = (draft.detail.data?.revision ?? 0) + 1
  const stale = draft.saveError?.code === 'stale'
  return (
    <div role="region" aria-label="Unsaved changes"
      className="sticky bottom-0 z-10 flex flex-wrap items-center gap-3 border-t border-accent/45 bg-tint px-4 py-3 md:px-6">
      <span className="font-semibold text-text">Unsaved changes</span>
      <label className="flex min-w-0 flex-1 items-center gap-2 text-sm text-text-muted">
        <span className="shrink-0">Note</span>
        <input value={note} maxLength={200} onChange={e => setNote(e.target.value)} placeholder="What changed (optional)"
          className="min-h-10 w-full min-w-0 border border-line-strong bg-console px-3 text-base text-text pointer-coarse:min-h-11" />
      </label>
      <div className="flex gap-2">
        <Button onClick={() => { draft.discard(); setNote('') }}>Discard</Button>
        <Button variant="primary" busy={draft.save.isPending} disabled={!draft.dirty}
          onClick={() => draft.save.mutate(note, { onSuccess: () => setNote('') })}>Save as revision {next}</Button>
      </div>
      {draft.saveError && (
        <p role="alert" className="m-0 w-full text-sm text-accent-text">
          {stale ? `${draft.saveError.message} Your edits are kept until you reload.` : draft.saveError.message}
        </p>
      )}
    </div>
  )
}
