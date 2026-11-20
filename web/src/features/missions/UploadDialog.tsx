import { useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type DragEvent } from 'react'
import { Button } from '../../components/Button'
import { errorMessage } from '../../lib/api'
import { formatBytes } from '../../lib/format'
import type { MissionDto } from '../../lib/types'
import { droppedFiles, pickedFiles, upload, type PickedFile } from './useMissions'

// Upload a mission (a .pbo, a .zip, or a dropped or picked folder). For an existing mission it saves the next version; either way it
// goes onto the servers (Phase 4a Ruling 5).
export function UploadForm({ mission, onDone }: { mission?: MissionDto; onDone: (missionId: string, version: number) => void }) {
  const [files, setFiles] = useState<PickedFile[]>([])
  const [progress, setProgress] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [over, setOver] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)
  const folderInput = useRef<HTMLInputElement>(null)
  const queryClient = useQueryClient()
  const total = files.reduce((n, f) => n + f.file.size, 0)
  const label = files.length === 1 ? files[0].path : files.length > 1 ? `${files[0].path.split('/')[0]} (${files.length} files)` : null

  const pick = (list: PickedFile[]) => { setFiles(list); setError(null) }
  async function drop(e: DragEvent) {
    e.preventDefault()
    setOver(false)
    pick(await droppedFiles(e.dataTransfer.items))
  }
  async function send() {
    setError(null)
    setProgress(0)
    try {
      const url = mission ? `/api/missions/${mission.id}/upload` : '/api/missions/upload'
      const r = await upload<{ mission: MissionDto; version: number }>(url, files, setProgress)
      await queryClient.invalidateQueries({ queryKey: ['missions'] })
      await queryClient.invalidateQueries({ queryKey: ['mission', r.mission.id] })
      onDone(r.mission.id, r.version)
    } catch (e) {
      setError(errorMessage(e))
      setProgress(null)
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <p className="m-0 text-sm text-text-secondary">
        {mission
          ? <>Upload {mission.className}.{mission.map} again as a .pbo, a .zip or its folder. It becomes the next version and goes onto the servers.</>
          : <>A mission is named <span className="font-mono">Name.Map</span>, like <span className="font-mono">co10_Escape.Altis</span>. It goes onto the servers as soon as it&apos;s uploaded.</>}
      </p>
      <div
        onDragOver={e => { e.preventDefault(); setOver(true) }} onDragLeave={() => setOver(false)} onDrop={drop}
        className={`flex min-h-32 flex-col items-center justify-center gap-3 border border-dashed px-4 py-6 text-center ${over ? 'border-accent bg-accent/10' : 'border-line-strong'}`}
      >
        <span className="text-sm text-text-secondary">{label ? <><span className="break-all font-mono text-text">{label}</span> · {formatBytes(total)}</> : 'Drop a .pbo, a .zip or a mission folder here'}</span>
        <div className="flex flex-wrap justify-center gap-2">
          <Button size="sm" onClick={() => fileInput.current?.click()}>Choose a .pbo or .zip</Button>
          <Button size="sm" onClick={() => folderInput.current?.click()}>Choose a folder</Button>
        </div>
        <input ref={fileInput} type="file" accept=".pbo,.zip" hidden onChange={e => e.target.files && pick(pickedFiles(e.target.files))} />
        <input ref={folderInput} type="file" hidden {...{ webkitdirectory: '' }} onChange={e => e.target.files && pick(pickedFiles(e.target.files))} />
      </div>
      {progress !== null && (
        <div className="flex items-center gap-3" role="status" aria-live="polite">
          <div className="h-1.5 flex-1 bg-line" aria-hidden><div className="h-full bg-accent transition-[width]" style={{ width: `${Math.round(progress * 100)}%` }} /></div>
          <span className="w-24 text-right text-sm tabular-nums text-text-secondary">{progress < 1 ? `${Math.round(progress * 100)}%` : 'Checking…'}</span>
        </div>
      )}
      {error && <p role="alert" className="m-0 text-sm text-accent-text">{error}</p>}
      <div className="flex justify-end">
        <Button variant="primary" disabled={files.length === 0} busy={progress !== null} onClick={send}>{mission ? 'Upload new version' : 'Upload mission'}</Button>
      </div>
    </div>
  )
}
