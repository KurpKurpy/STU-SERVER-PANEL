import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router'
import { Button } from '../../components/Button'
import { Field } from '../../components/Field'
import { api, errorMessage, safeNext } from '../../lib/api'
import type { LoginStatus } from '../../lib/types'
import { AuthFrame, FormError } from './AuthFrame'

const messages: Partial<Record<LoginStatus, string>> = {
  invalid: 'Wrong callsign or password.',
  lockedOut: 'Too many attempts. Try again in 15 minutes.',
  disabled: 'This account is disabled. Ask an admin.',
}

export function LoginPage() {
  const [params] = useSearchParams()
  const next = safeNext(params.get('next'))
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const setup = useQuery({ queryKey: ['setup'], queryFn: () => api<{ needed: boolean }>('/api/setup') })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (setup.data?.needed) return <Navigate to="/setup" replace />

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    setBusy(true)
    setError(null)
    try {
      const { status } = await api<{ status: LoginStatus }>('/api/auth/login', { json: { userName: form.get('userName'), password: form.get('password') } })
      if (status === 'ok') {
        await queryClient.invalidateQueries({ queryKey: ['me'] })
        navigate(next, { replace: true })
      } else if (status === 'twoFactor') {
        navigate(`/login/2fa?next=${encodeURIComponent(next)}`)
      } else {
        setError(messages[status] ?? 'Sign-in failed.')
      }
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthFrame title="Sign in">
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        <Field label="Callsign" name="userName" autoComplete="username" required autoFocus />
        <Field label="Password" name="password" type="password" autoComplete="current-password" required />
        <FormError message={error} />
        <Button type="submit" variant="primary" busy={busy}>Sign in</Button>
      </form>
      <p className="m-0 text-sm text-text-muted">
        Forgot your password? Ask an admin for a reset link. New here? Use the invite link you were sent, or <Link to="/setup" className="text-accent-text">first-time setup</Link> on a fresh install.
      </p>
    </AuthFrame>
  )
}
