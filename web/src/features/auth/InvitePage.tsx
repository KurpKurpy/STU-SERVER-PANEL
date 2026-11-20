import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { Button } from '../../components/Button'
import { Field } from '../../components/Field'
import { ErrorNote, Loading, formatWhen } from '../../components/Feedback'
import { RoleChip } from '../../components/StatusBlock'
import { ApiError, api, errorMessage } from '../../lib/api'
import type { InviteInfo } from '../../lib/types'
import { AuthFrame, FormError } from './AuthFrame'

const reasons: Record<string, string> = {
  expired: 'This invite has expired. Ask an admin for a new link.',
  used: 'This invite has already been used. If that was you, sign in instead.',
  revoked: 'This invite was cancelled. Ask an admin for a new link.',
  unknown: "This invite link isn't valid. Check you copied all of it.",
}

export function InvitePage() {
  const { token = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const info = useQuery({ queryKey: ['invite', token], queryFn: () => api<InviteInfo>(`/api/invites/${encodeURIComponent(token)}`) })
  const [errors, setErrors] = useState<{ userName?: string; password?: string; form?: string }>({})
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    if (form.get('password') !== form.get('confirm')) { setErrors({ password: "The passwords don't match." }); return }
    setBusy(true)
    setErrors({})
    try {
      await api(`/api/invites/${encodeURIComponent(token)}/accept`, { json: { userName: form.get('userName'), displayName: form.get('displayName'), password: form.get('password') } })
      await queryClient.invalidateQueries({ queryKey: ['me'] })
      navigate('/', { replace: true })
    } catch (err) {
      if (err instanceof ApiError && err.code === 'conflict_username') setErrors({ userName: err.message })
      else if (err instanceof ApiError && err.code === 'validation') setErrors({ password: err.message })
      else if (err instanceof ApiError && err.code === 'invite_invalid') void info.refetch()
      else setErrors({ form: errorMessage(err) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthFrame title="Join the unit">
      {info.isPending && <Loading />}
      {info.error && <ErrorNote error={info.error} onRetry={() => info.refetch()} />}
      {info.data && !info.data.valid && (
        <>
          <p role="alert" className="m-0 text-accent-text">{reasons[info.data.reason ?? 'unknown']}</p>
          <Link to="/login" className="text-accent-text">Go to sign in</Link>
        </>
      )}
      {info.data?.valid && (
        <>
          <p className="m-0 flex flex-wrap items-center gap-2 text-text-secondary">
            You're joining as <RoleChip>{info.data.roleName}</RoleChip>
            <span className="text-sm text-text-muted">· link expires {formatWhen(info.data.expiresAt)}</span>
          </p>
          <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
            <Field label="Callsign" name="userName" autoComplete="username" required autoFocus error={errors.userName} hint="Use your unit callsign. You sign in with this." />
            <Field label="Display name" name="displayName" required />
            <Field label="Password" name="password" type="password" autoComplete="new-password" minLength={12} required error={errors.password} hint="At least 12 characters." />
            <Field label="Confirm password" name="confirm" type="password" autoComplete="new-password" required />
            <FormError message={errors.form ?? null} />
            <Button type="submit" variant="primary" busy={busy}>Create my account</Button>
          </form>
        </>
      )}
    </AuthFrame>
  )
}
