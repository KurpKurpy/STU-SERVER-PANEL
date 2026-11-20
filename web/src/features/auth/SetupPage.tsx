import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Navigate, useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { Field } from '../../components/Field'
import { Loading } from '../../components/Feedback'
import { api, errorMessage } from '../../lib/api'
import { AuthFrame, FormError } from './AuthFrame'

export function SetupPage() {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const setup = useQuery({ queryKey: ['setup'], queryFn: () => api<{ needed: boolean }>('/api/setup') })
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (setup.isPending) return <AuthFrame title="First-time setup"><Loading /></AuthFrame>
  if (setup.data && !setup.data.needed) return <Navigate to="/login" replace />

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = new FormData(e.currentTarget)
    if (form.get('password') !== form.get('confirm')) { setError("The passwords don't match."); return }
    setBusy(true)
    setError(null)
    try {
      await api('/api/setup', { json: { token: form.get('token'), userName: form.get('userName'), displayName: form.get('displayName'), password: form.get('password') } })
      queryClient.removeQueries({ queryKey: ['setup'] })
      await queryClient.invalidateQueries({ queryKey: ['me'] })
      navigate('/', { replace: true })
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  return (
    <AuthFrame title="First-time setup">
      <p className="m-0 text-text-secondary">Create the Owner account. Only someone with access to the server can do this.</p>
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        <Field label="Setup token" name="token" autoComplete="off" required autoFocus
          hint={<>In <code className="font-mono">data/setup-token.txt</code> next to the panel, or in its console output.</>} />
        <Field label="Callsign" name="userName" autoComplete="username" required hint="Letters, numbers, - _ and . only. You sign in with this." />
        <Field label="Display name" name="displayName" required />
        <Field label="Password" name="password" type="password" autoComplete="new-password" minLength={12} required hint="At least 12 characters. A short sentence works well." />
        <Field label="Confirm password" name="confirm" type="password" autoComplete="new-password" required />
        <FormError message={error} />
        <Button type="submit" variant="primary" busy={busy}>Create Owner account</Button>
      </form>
    </AuthFrame>
  )
}
