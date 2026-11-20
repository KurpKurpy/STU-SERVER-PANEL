import { useQuery } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { ApiError, api } from './api'
import type { MeDto, PublicSettings } from './types'
import { ErrorNote, Loading, NoAccess } from '../components/Feedback'

export function useMe() {
  return useQuery({ queryKey: ['me'], queryFn: () => api<MeDto>('/api/me'), retry: false, staleTime: 30_000 })
}

// "files.*" means any permission starting "files." (the file manager has one per folder).
export function useCan() {
  const { data } = useMe()
  return (permission: string) => permission.endsWith('*')
    ? !!data?.permissions.some(p => p.startsWith(permission.slice(0, -1)))
    : !!data?.permissions.includes(permission)
}

export function useAppName() {
  const { data } = useQuery({ queryKey: ['settings', 'public'], queryFn: () => api<PublicSettings>('/api/settings/public'), staleTime: 300_000 })
  return data?.appName ?? '13th Console'
}

// Gate for every signed-in screen: 401 → login, 2FA-required role without 2FA → enrolment.
export function RequireAuth() {
  const { data, error, isPending, refetch } = useMe()
  const location = useLocation()
  if (isPending) return <Loading />
  if (error instanceof ApiError && error.status === 401)
    return <Navigate to={`/login?next=${encodeURIComponent(location.pathname + location.search)}`} replace />
  if (error || !data) return <div className="p-6"><ErrorNote error={error} onRetry={() => refetch()} /></div>
  if (data.mustEnrolTwoFactor && location.pathname !== '/account/2fa') return <Navigate to="/account/2fa" replace />
  return <Outlet />
}

export function RequirePermission({ perm, children }: { perm: string; children: ReactNode }) {
  const can = useCan()
  return can(perm) ? <>{children}</> : <NoAccess />
}
