import { useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router'
import { Button } from '../../components/Button'
import { Field } from '../../components/Field'
import { ApiError, api, errorMessage } from '../../lib/api'
import { AuthFrame, FormError } from './AuthFrame'

export function ResetPage() {
  const [params] = useSearchParams()
  const [done, setDone] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const userId = params.get('u')
  const token = params.get('t')

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    if (form.get('password') !== form.get('confirm')) { setError("The passwords don't match."); return }
    setBusy(true)
    setError(null)
    try {
      await api('/api/auth/reset', { json: { userId, token, newPassword: form.get('password') } })
      setDone(true)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthFrame title="Set a new password">
      {!userId || !token ? (
        <p role="alert" className="m-0 text-accent-text">This reset link is incomplete. Copy the whole link from the admin's message.</p>
      ) : done ? (
        <>
          <p role="status" className="m-0">Password changed. Sign in with your new password.</p>
          <Link to="/login" className="text-accent-text">Go to sign in</Link>
        </>
      ) : (
        <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
          <Field label="New password" name="password" type="password" autoComplete="new-password" minLength={12} required autoFocus hint="At least 12 characters." />
          <Field label="Confirm password" name="confirm" type="password" autoComplete="new-password" required />
          <FormError message={error} />
          <Button type="submit" variant="primary" busy={busy}>Save password</Button>
        </form>
      )}
    </AuthFrame>
  )
}
