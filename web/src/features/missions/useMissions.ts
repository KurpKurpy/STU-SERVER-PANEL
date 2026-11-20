import { useQuery } from '@tanstack/react-query'
import { ApiError, api } from '../../lib/api'
import type { DeploymentDto, MissionDto, MissionFileDto, VersionDto } from '../../lib/types'

export const categories = ['Main Operations', 'Training', 'Zeus Operations', 'Campaigns', 'Side Operations', 'Testing']

export interface MissionFilter { q: string; map: string; category: string; tag: string; archived: boolean }

export function useMissionList(filter: MissionFilter, enabled = true) {
  const params = new URLSearchParams()
  if (filter.q.trim()) params.set('q', filter.q.trim())
  if (filter.map) params.set('map', filter.map)
  if (filter.category) params.set('category', filter.category)
  if (filter.tag) params.set('tag', filter.tag)
  if (filter.archived) params.set('archived', 'true')
  const qs = params.toString()
  return useQuery({ queryKey: ['missions', qs], queryFn: () => api<MissionDto[]>(`/api/missions${qs ? `?${qs}` : ''}`), enabled })
}

export const useMission = (id: string) => useQuery({ queryKey: ['mission', id], queryFn: () => api<MissionDto>(`/api/missions/${id}`) })
export const useVersions = (id: string) => useQuery({ queryKey: ['mission', id, 'versions'], queryFn: () => api<VersionDto[]>(`/api/missions/${id}/versions`) })
export const useFiles = (id: string) => useQuery({ queryKey: ['mission', id, 'files'], queryFn: () => api<MissionFileDto[]>(`/api/missions/${id}/files`) })

// Refetched every few seconds while a write is still Pending or Waiting on an install.
export function useDeployments(id: string) {
  return useQuery({
    queryKey: ['mission', id, 'deployments'],
    queryFn: () => api<DeploymentDto[]>(`/api/missions/${id}/deployments`),
    refetchInterval: q => (q.state.data?.some(d => d.status === 'Pending' || d.status === 'Waiting') ? 3000 : false),
  })
}

export interface PickedFile { file: File; path: string }

// A multipart upload with real progress (fetch can't report upload progress). Each file is sent under its relative path.
export function upload<T>(url: string, files: PickedFile[], onProgress: (fraction: number) => void, signal?: AbortSignal,
  tooLarge = 'The upload is larger than 200 MB.'): Promise<T> {
  return new Promise((resolve, reject) => {
    const form = new FormData()
    for (const f of files) form.append('files', f.file, f.path)
    const xhr = new XMLHttpRequest()
    xhr.open('POST', url)
    xhr.setRequestHeader('X-Console', '1')
    xhr.withCredentials = true
    xhr.upload.onprogress = e => { if (e.lengthComputable) onProgress(e.loaded / e.total) }
    xhr.onload = () => {
      let body: { code?: string; message?: string; field?: string } | undefined
      try { body = xhr.responseText ? JSON.parse(xhr.responseText) : undefined } catch { body = undefined }
      if (xhr.status >= 200 && xhr.status < 300) resolve(body as T)
      else reject(new ApiError(xhr.status, body?.code ?? `http_${xhr.status}`, body?.message ?? (xhr.status === 413 ? tooLarge : 'The upload failed. Try again.'), body?.field))
    }
    xhr.onerror = () => reject(new ApiError(0, 'network', 'The upload was interrupted. Check the connection and try again.'))
    signal?.addEventListener('abort', () => xhr.abort())
    xhr.send(form)
  })
}

// Files dropped on the page, folders included (read recursively with their paths).
export async function droppedFiles(items: DataTransferItemList): Promise<PickedFile[]> {
  const entries = Array.from(items).map(i => i.webkitGetAsEntry()).filter((e): e is FileSystemEntry => e !== null)
  const out: PickedFile[] = []
  async function walk(entry: FileSystemEntry): Promise<void> {
    if (entry.isFile) {
      const file = await new Promise<File>((res, rej) => (entry as FileSystemFileEntry).file(res, rej))
      out.push({ file, path: entry.fullPath.replace(/^\//, '') })
      return
    }
    const reader = (entry as FileSystemDirectoryEntry).createReader()
    for (;;) {
      const batch = await new Promise<FileSystemEntry[]>((res, rej) => reader.readEntries(res, rej))
      if (batch.length === 0) break
      for (const child of batch) await walk(child)
    }
  }
  for (const e of entries) await walk(e)
  return out
}

// <input type="file"> picks: a folder pick carries webkitRelativePath; a single .pbo or .zip doesn't.
export const pickedFiles = (list: FileList): PickedFile[] =>
  Array.from(list).map(file => ({ file, path: file.webkitRelativePath || file.name }))
