import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '../../components/Button'
import { CopyField } from '../../components/CopyField'
import { ErrorNote, Loading } from '../../components/Feedback'
import { Field } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { StatusBlock } from '../../components/StatusBlock'
import { api, errorMessage } from '../../lib/api'
import { useMe } from '../../lib/auth'
import type { RecoveryCodes, TwoFactorSetup } from '../../lib/types'

function downloadCodes(codes: string[]) {
  const blob = new Blob([`13th Console recovery codes\nEach works once.\n\n${codes.join('\n')}\n`], { type: 'text/plain' })
  const a = Object.assign(document.createElement('a'), { href: URL.createObjectURL(blob), download: '13th-console-recovery-codes.txt' })
  a.click()
  URL.revokeObjectURL(a.href)
}

export function EnrolTwoFactorPage() {
  const { data: me } = useMe()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const enrolled = !!me?.twoFactorEnabled
  const setup = useQuery({ queryKey: ['2fa-setup'], queryFn: () => api<TwoFactorSetup>('/api/account/2fa'), enabled: !!me && !enrolled, staleTime: Infinity })
  const [codes, setCodes] = useState<string[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const result = await api<RecoveryCodes>('/api/account/2fa', { json: { code: new FormData(e.currentTarget).get('code') } })
      setCodes(result.codes)
    } catch (err) {
      setError(errorMessage(err))
    } finally {
      setBusy(false)
    }
  }

  async function finish() {
    await queryClient.invalidateQueries({ queryKey: ['me'] })
    navigate('/', { replace: true })
  }

  if (codes) return (
    <Panel title="Save your recovery codes" className="max-w-2xl">
      <div className="flex flex-col gap-4 px-6 pt-5 pb-6">
        <p className="m-0">Two-factor sign-in is on. If you lose your phone, each of these codes gets you in once. They won't be shown again.</p>
        <ul className="m-0 grid list-none grid-cols-2 gap-2 p-0 font-mono text-base sm:grid-cols-5">
          {codes.map(c => <li key={c} className="border border-line-strong px-2 py-1.5 text-center">{c}</li>)}
        </ul>
        <div className="flex flex-wrap gap-2.5">
          <Button onClick={() => downloadCodes(codes)}>Download as text</Button>
          <Button variant="primary" onClick={finish}>I've saved them</Button>
        </div>
      </div>
    </Panel>
  )

  return (
    <Panel title="Two-factor sign-in" meta={enrolled ? <StatusBlock tone="ok">On</StatusBlock> : <StatusBlock tone="warn">Off</StatusBlock>} className="max-w-2xl"
      emphasis={!!me?.mustEnrolTwoFactor}>
      <div className="flex flex-col gap-4 px-6 pt-5 pb-6">
        {enrolled ? (
          <p className="m-0">Two-factor sign-in is on for your account.</p>
        ) : (
          <>
            {me?.mustEnrolTwoFactor && (
              <p role="status" className="m-0 border border-warn/60 bg-warn/5 px-3 py-2">Your role requires two-factor sign-in. Set it up to continue.</p>
            )}
            <ol className="m-0 flex list-decimal flex-col gap-3 pl-5">
              <li>Open an authenticator app on your phone (any app that supports 6-digit codes).</li>
              <li>Add an account with this key, or open the link on your phone.</li>
              <li>Enter the 6-digit code it shows.</li>
            </ol>
            {setup.isPending && <Loading />}
            {setup.error && <ErrorNote error={setup.error} onRetry={() => setup.refetch()} />}
            {setup.data && (
              <>
                <CopyField label="Setup key" value={setup.data.sharedKey.match(/.{1,4}/g)?.join(' ') ?? setup.data.sharedKey} />
                <a href={setup.data.authenticatorUri} className="self-start text-accent-text">Open in authenticator app</a>
                <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
                  <Field label="6-digit code" name="code" inputMode="numeric" autoComplete="one-time-code" required error={error ?? undefined} />
                  <Button type="submit" variant="primary" busy={busy} className="self-start">Turn on two-factor</Button>
                </form>
              </>
            )}
          </>
        )}
      </div>
    </Panel>
  )
}
