import { useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate, useSearchParams } from 'react-router'
import { Button } from '../../components/Button'
import { Field } from '../../components/Field'
import { api, errorMessage, safeNext } from '../../lib/api'
import type { LoginStatus } from '../../lib/types'
import { AuthFrame, FormError } from './AuthFrame'

export function TwoFactorLoginPage() {
  const [params] = useSearchParams()
  const next = safeNext(params.get('next'))
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const [recovery, setRecovery] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const value = String(new FormData(e.currentTarget).get('code') ?? '')
    setBusy(true)
    setError(null)
    try {
      const { status } = await api<{ status: LoginStatus }>('/api/auth/2fa', { json: recovery ? { recoveryCode: value } : { code: value } })
      if (status === 'ok') {
        await queryClient.invalidateQueries({ queryKey: ['me'] })
        navigate(next, { replace: true })
      } else {
        setError(status === 'lockedOut' ? 'Too many attempts. Try again in 15 minutes.' : "That code didn't work. Sign in again if this keeps happening.")
      }
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthFrame title="Two-factor check">
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        {recovery
          ? <Field key="r" label="Recovery code" name="code" autoComplete="off" required autoFocus hint="Each recovery code works once." />
          : <Field key="c" label="6-digit code" name="code" inputMode="numeric" autoComplete="one-time-code" pattern="[0-9 ]*" required autoFocus hint="From your authenticator app." />}
        <FormError message={error} />
        <Button type="submit" variant="primary" busy={busy}>Verify</Button>
      </form>
      <button type="button" onClick={() => { setRecovery(r => !r); setError(null) }} className="min-h-11 self-start text-left text-sm text-accent-text underline">
        {recovery ? 'Use the authenticator code instead' : 'Lost your phone? Use a recovery code'}
      </button>
    </AuthFrame>
  )
}
