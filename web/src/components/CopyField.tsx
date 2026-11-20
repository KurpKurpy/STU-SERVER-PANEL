import { useRef, useState } from 'react'
import { Button } from './Button'

export function CopyField({ value, label }: { value: string; label: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'manual'>('idle')
  const textRef = useRef<HTMLSpanElement>(null)

  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setState('copied')
    } catch {
      // Clipboard needs HTTPS outside localhost: select the text so it can be copied by hand.
      const range = document.createRange()
      if (textRef.current) range.selectNodeContents(textRef.current)
      getSelection()?.removeAllRanges()
      getSelection()?.addRange(range)
      setState('manual')
    }
    setTimeout(() => setState('idle'), 2500)
  }

  return (
    <div className="flex min-h-11 items-center gap-2.5 border border-line-strong bg-black/35 pr-1.5 pl-3">
      <span ref={textRef} className="flex-1 truncate font-mono text-sm" title={value}>{value}</span>
      <Button size="sm" variant="primary" onClick={copy} aria-label={`Copy ${label}`}>{state === 'copied' ? 'Copied' : 'Copy'}</Button>
      <span aria-live="polite" className="sr-only">
        {state === 'copied' ? `${label} copied` : state === 'manual' ? `${label} selected, press Ctrl+C to copy` : ''}
      </span>
    </div>
  )
}
