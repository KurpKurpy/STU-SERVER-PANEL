import type { FileDiff } from '../lib/types'

// A plain line diff per file (Monaco's diff view comes with Phase 4). Colour always comes with a +/− mark and hidden words.
export function DiffView({ files }: { files: FileDiff[] }) {
  return (
    <div className="flex flex-col gap-4">
      {files.map(f => {
        const changed = f.lines.length > 0
        return (
          <section key={f.name} aria-label={f.name} className="flex min-w-0 flex-col gap-1.5">
            <h3 className="m-0 flex flex-wrap items-center gap-2 text-sm font-semibold text-text-secondary">
              <span className="font-mono">{f.name}</span>
              <span className="text-xs font-normal text-text-muted">{!changed ? 'No changes' : f.oldExists ? 'Changed' : 'New file'}</span>
            </h3>
            {changed && (
              <div className="relative max-h-[28rem] overflow-auto border border-line bg-console">
                <table className="w-full border-collapse font-mono text-sm">
                  <tbody>
                    {f.lines.map((l, i) => l.kind === '…' ? (
                      <tr key={i}><td colSpan={4} className="px-3 py-1 text-xs text-text-muted">… {l.text}</td></tr>
                    ) : (
                      <tr key={i} className={l.kind === '+' ? 'bg-ok/10' : l.kind === '-' ? 'bg-bad/10' : ''}>
                        <td className="w-10 px-2 text-right text-text-muted select-none">{l.oldLine ?? ''}</td>
                        <td className="w-10 px-2 text-right text-text-muted select-none">{l.newLine ?? ''}</td>
                        <td className={`w-5 text-center select-none ${l.kind === '+' ? 'text-ok' : l.kind === '-' ? 'text-bad' : 'text-text-muted'}`}>
                          <span aria-hidden="true">{l.kind === '-' ? '−' : l.kind === '+' ? '+' : ''}</span>
                          <span className="sr-only">{l.kind === '+' ? 'added' : l.kind === '-' ? 'removed' : ''}</span>
                        </td>
                        <td className="pr-3 whitespace-pre text-text-secondary">{l.text}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        )
      })}
    </div>
  )
}
