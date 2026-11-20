import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../../lib/api'
import { useCan } from '../../lib/auth'
import { useJobsLive } from '../../lib/hub'
import type { JobDto, JobStatus } from '../../lib/types'

export const openStatuses: JobStatus[] = ['Queued', 'Running', 'NeedsInput']
export const isOpen = (j: JobDto) => openStatuses.includes(j.status)

// The recent jobs, kept live from the hub; a finished job refreshes the mod library and mod lists.
export function useJobs() {
  const can = useCan()
  const queryClient = useQueryClient()
  const enabled = can('mods.view')
  const jobs = useQuery({ queryKey: ['jobs'], queryFn: () => api<JobDto[]>('/api/jobs'), enabled })
  const [tails, setTails] = useState<Record<string, string[]>>({})

  useJobsLive(enabled, (job, lines) => {
    queryClient.setQueryData<JobDto[]>(['jobs'], prev =>
      [job, ...(prev ?? []).filter(j => j.id !== job.id)].sort((a, b) => b.createdAt.localeCompare(a.createdAt)))
    if (lines.length) setTails(t => ({ ...t, [job.id]: [...(t[job.id] ?? []), ...lines].slice(-200) }))
    if (!openStatuses.includes(job.status))
      for (const key of ['mods', 'collections', 'keys']) void queryClient.invalidateQueries({ queryKey: [key] })
  })

  return { jobs: jobs.data ?? [], tails, error: jobs.error }
}
