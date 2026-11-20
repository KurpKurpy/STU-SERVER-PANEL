import { Check, Field, TextArea } from '../../components/Field'
import { Panel } from '../../components/Panel'
import { useCan } from '../../lib/auth'
import { useProfile } from './ProfileLayout'
import { useListText } from './SettingsForm'

const toAddresses = (t: string) => t.split('\n').map(a => a.trim()).filter(Boolean)

// Headless clients run beside the server and are never started automatically unless "Start with the server" is on (source spec §14).
export function HeadlessTab() {
  const { draft } = useProfile()
  const can = useCan()
  const readOnly = !can('profiles.manage')
  const hc = draft.snapshot!.settings.headless
  const set = (change: (h: typeof hc) => void) => draft.update(x => change(x.settings.headless))
  const lines = draft.preview?.command.headless ?? []
  const [addresses, setAddresses] = useListText(hc.addresses ?? ['127.0.0.1'], toAddresses)
  return (
    <div className="grid grid-cols-1 gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
      <Panel title="Headless clients">
        <div className="flex flex-col gap-4 px-6 py-5">
          <p role="note" className="m-0 border border-warn/40 bg-black/20 px-4 py-3 text-sm">
            Only enable headless clients for missions that have headless client slots. Without a slot they start but never join.
          </p>
          <Check label="Enable headless clients" disabled={readOnly} checked={hc.enabled}
            hint="Adds headlessClients[] and localClient[] to server.cfg." onChange={e => set(h => { h.enabled = e.target.checked })} />
          <Field label="How many" type="number" min={1} max={8} disabled={readOnly || !hc.enabled} value={String(hc.count)}
            error={draft.fieldErrors['headless.count']} onChange={e => set(h => { h.count = Number(e.target.value) || 1 })} />
          <Check label="Start with the server" disabled={readOnly || !hc.enabled} checked={hc.withServer}
            hint="Otherwise start them from the server page when a mission needs them." onChange={e => set(h => { h.withServer = e.target.checked })} />
          <TextArea label="Addresses (one per line)" rows={3} disabled={readOnly || !hc.enabled} value={addresses}
            error={draft.fieldErrors['headless.addresses']} className="[&_textarea]:font-mono"
            hint="IPv4 addresses allowed to connect as headless clients. The clients on this machine connect to the first one."
            onChange={e => { setAddresses(e.target.value); set(h => { h.addresses = toAddresses(e.target.value) }) }} />
        </div>
      </Panel>
      <Panel title="Launch lines">
        <div className="px-6 py-5">
          {lines.length === 0 ? <p className="m-0 text-text-muted">Headless clients are off.</p> : (
            <div className="relative overflow-x-auto border border-line bg-console">
              <pre className="m-0 p-3 font-mono text-sm whitespace-pre-wrap break-all text-text-secondary">{lines.join('\n\n')}</pre>
            </div>
          )}
        </div>
      </Panel>
    </div>
  )
}
