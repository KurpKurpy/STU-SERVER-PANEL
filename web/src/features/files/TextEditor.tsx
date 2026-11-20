import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { Button } from '../../components/Button'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Panel } from '../../components/Panel'
import { ApiError, api, errorMessage } from '../../lib/api'
import type { FileInstall, FileSpaceInfo, FileText } from '../../lib/types'
import { CodeEditor } from '../missions/CodeView'
import { fileQuery } from './paths'

// Phase 4b Ruling 13: a UTF-8 text file up to 1 MB. The BOM goes back as it was; the save only lands on the version that was opened.
export function TextEditor({ install, space, path, readOnly, onClose }: {
  install: FileInstall; space: FileSpaceInfo; path: string; readOnly: boolean; onClose: () => void
}) {
  const file = useQuery({
    queryKey: ['file-text', install.id, space.name, path],
    queryFn: () => api<FileText>(`/api/files/text?${fileQuery(install.id, space.name, path)}`),
    staleTime: Infinity, gcTime: 0,
  })
  const [value, setValue] = useState('')
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ text: string; bad?: boolean; conflict?: boolean } | null>(null)
  useEffect(() => { if (file.data) setValue(file.data.text) }, [file.data])

  const changed = !!file.data && value !== file.data.text
  async function save() {
    if (!file.data || !changed || readOnly) return
    setSaving(true)
    setMessage(null)
    try {
      const r = await api<{ backup?: string; message?: string }>('/api/files/text', {
        method: 'PUT', json: { install: install.id, space: space.name, path, text: value, bom: file.data.bom, sha256: file.data.sha256 },
      })
      setMessage({ text: r?.message ?? (r?.backup ? `Saved. The old copy is in backups\\${r.backup}.` : 'Saved.') })
      await file.refetch()
    } catch (e) {
      const conflict = e instanceof ApiError && e.message.includes('changed since')
      setMessage({ text: conflict ? 'Someone changed this file since you opened it. Reload to see their version; your edits here are lost on reload.' : errorMessage(e), bad: true, conflict })
    } finally { setSaving(false) }
  }

  return (
    <Panel title={path.slice(path.lastIndexOf('\\') + 1)} meta={
      <div className="flex flex-wrap gap-2">
        {!readOnly && <Button variant="primary" busy={saving} disabled={!changed} onClick={save}>Save</Button>}
        <Button onClick={() => { if (!changed || window.confirm('Close without saving your changes?')) onClose() }}>Close</Button>
      </div>
    }>
      <p className="m-0 break-all px-6 pt-3 pb-2 font-mono text-xs text-text-muted">{space.label} \ {path}{readOnly ? ' · read-only' : ''}</p>
      {message && (
        <div role={message.bad ? 'alert' : 'status'} className={`flex flex-wrap items-center gap-3 px-6 pb-3 text-sm ${message.bad ? 'text-accent-text' : 'text-text-secondary'}`}>
          {message.text}
          {message.conflict && <Button size="sm" onClick={async () => { await file.refetch(); setMessage(null) }}>Reload</Button>}
        </div>
      )}
      {file.isPending && <div className="px-6 py-5"><Loading /></div>}
      {file.error && <div className="px-6 py-5"><ErrorNote error={file.error} /></div>}
      {file.data && (
        <div className="h-[70vh] border-t border-line">
          <CodeEditor path={path.replace(/\\/g, '/')} value={value} readOnly={readOnly} onChange={setValue} onSave={save} />
        </div>
      )}
    </Panel>
  )
}
