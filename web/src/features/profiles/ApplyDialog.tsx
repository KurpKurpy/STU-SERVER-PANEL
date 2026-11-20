import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Button } from '../../components/Button'
import { DiffView } from '../../components/DiffView'
import { ErrorNote, Loading } from '../../components/Feedback'
import { ApiError, api, errorMessage } from '../../lib/api'
import type { ApplyPreview } from '../../lib/types'

// What Apply would change on the server, then the confirmed write (the agent backs the files up first).
export function ApplyDialog({ profileId, onDone }: { profileId: string; onDone: () => void }) {
  const queryClient = useQueryClient()
  const preview = useQuery({
    queryKey: ['apply-preview', profileId], queryFn: () => api<ApplyPreview>(`/api/profiles/${profileId}/apply-preview`), staleTime: 0, gcTime: 0,
  })
  const apply = useMutation({
    mutationFn: (revision: number) => api(`/api/profiles/${profileId}/apply`, { json: { revision } }),
    onSuccess: async () => {
      await Promise.all(['profile', 'profiles', 'servers', 'revisions'].map(k => queryClient.invalidateQueries({ queryKey: [k] })))
      onDone()
    },
    onError: e => { if (e instanceof ApiError && e.code === 'stale') void preview.refetch() },
  })
  if (preview.isPending) return <Loading label="Reading the files on the server…" />
  if (preview.error) return <ErrorNote error={preview.error} onRetry={() => preview.refetch()} />
  const p = preview.data
  return (
    <>
      <p className="m-0 max-w-prose text-text-secondary">
        Writes revision {p.revision}. Backs up the current files on the server first. Takes effect the next time the server starts.
      </p>
      <DiffView files={p.files} />
      {!p.canApply && <p role="alert" className="m-0 text-sm text-accent-text">{p.reason}</p>}
      {apply.error && <p role="alert" className="m-0 text-sm text-accent-text">{errorMessage(apply.error)}</p>}
      <div className="flex justify-end gap-2.5">
        <Button onClick={onDone}>Cancel</Button>
        <Button variant="primary" disabled={!p.canApply} busy={apply.isPending} onClick={() => apply.mutate(p.revision)}>Apply revision {p.revision}</Button>
      </div>
    </>
  )
}
