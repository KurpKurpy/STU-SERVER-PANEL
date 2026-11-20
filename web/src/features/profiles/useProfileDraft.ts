import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { ApiError, api, errorMessage } from '../../lib/api'
import type { CfgKey, ConfigFile, JsonValue, PreviewResult, ProfileDetail, ProfileSnapshot, SaveResult, SecretsWrite } from '../../lib/types'

export const useCatalog = () =>
  useQuery({ queryKey: ['profile-catalog'], queryFn: () => api<{ keys: CfgKey[] }>('/api/profiles/catalog').then(r => r.keys), staleTime: Infinity })

const fileKey = (file: ConfigFile) => file as 'server' | 'basic' | 'difficulty' | 'launch'

// The profile being edited, shared by every tab: unsaved edits, a debounced live preview, and Save as one new revision.
export function useProfileDraft(id: string) {
  const queryClient = useQueryClient()
  const detail = useQuery({ queryKey: ['profile', id], queryFn: () => api<ProfileDetail>(`/api/profiles/${id}`) })
  const [draft, setDraft] = useState<ProfileSnapshot | null>(null)
  // The revision the draft was cut from: a refetch mid-edit must not move it, or the save overwrites the newer revision.
  const [base, setBase] = useState<number>()
  const [secrets, setSecrets] = useState<SecretsWrite>({})
  const snapshot = draft ?? detail.data?.snapshot ?? null
  const dirty = draft !== null || Object.keys(secrets).length > 0

  function update(change: (s: ProfileSnapshot) => void) {
    if (!snapshot) return
    if (draft === null) setBase(detail.data?.revision)
    const next = structuredClone(snapshot)
    change(next)
    setDraft(next)
  }

  function setValue(file: ConfigFile, key: string, value: JsonValue | undefined) {
    update(s => {
      const values = s.settings[fileKey(file)]
      if (value === undefined) delete values[key]
      else values[key] = value
    })
  }

  // Live preview of the generated files and command line, 400 ms after the last edit.
  const [preview, setPreview] = useState<{ data?: PreviewResult; error?: ApiError | Error }>({})
  const key = useMemo(() => (snapshot ? JSON.stringify(snapshot) : ''), [snapshot])
  useEffect(() => {
    if (!key) return
    let current = true
    const timer = setTimeout(() => {
      api<PreviewResult>(`/api/profiles/${id}/preview`, { json: { snapshot: JSON.parse(key) } })
        .then(data => { if (current) setPreview({ data }) })
        .catch(error => { if (current) setPreview(p => ({ data: p.data, error })) })
    }, 400)
    return () => { current = false; clearTimeout(timer) }
  }, [id, key])

  const save = useMutation({
    mutationFn: (note: string) => api<SaveResult>(`/api/profiles/${id}/config`, {
      method: 'PUT', json: { baseRevision: draft ? base : detail.data?.revision, note: note.trim() || null, snapshot, secrets },
    }),
    onSuccess: async () => {
      setDraft(null)
      setSecrets({})
      await Promise.all(['profile', 'profiles', 'servers', 'revisions'].map(k => queryClient.invalidateQueries({ queryKey: [k] })))
    },
  })

  const saveError = save.error instanceof ApiError ? save.error : save.error ? new ApiError(0, 'error', errorMessage(save.error)) : null
  const fieldErrors: Record<string, string> = {}
  for (const e of [saveError, preview.error instanceof ApiError ? preview.error : null])
    if (e?.field && !fieldErrors[e.field]) fieldErrors[e.field] = e.message

  return {
    detail, snapshot, dirty, update, setValue, secrets, setSecrets, preview: preview.data, previewError: preview.error,
    save, saveError, fieldErrors, discard: () => { setDraft(null); setSecrets({}); save.reset() },
  }
}

export type ProfileDraft = ReturnType<typeof useProfileDraft>
