import Editor, { DiffEditor, loader } from '@monaco-editor/react'
import { useEffect, useRef } from 'react'
import * as monaco from 'monaco-editor'
import editorWorker from 'monaco-editor/editor/editor.worker?worker'
import jsonWorker from 'monaco-editor/language/json/json.worker?worker'

// Monaco from our own bundle, never a CDN (Phase 4a Ruling 13). Loaded only with the editor and the version diff.
self.MonacoEnvironment = { getWorker: (_id, label) => (label === 'json' ? new jsonWorker() : new editorWorker()) }
loader.config({ monaco })

const sqfKeywords = ['if', 'then', 'else', 'exitWith', 'for', 'from', 'to', 'step', 'do', 'while', 'switch', 'case', 'default', 'forEach', 'forEachReversed',
  'private', 'params', 'call', 'spawn', 'execVM', 'compile', 'preprocessFileLineNumbers', 'true', 'false', 'nil', 'this', 'with', 'try', 'catch', 'throw',
  'waitUntil', 'sleep', 'uiSleep', 'isNil', 'select', 'apply', 'findIf', 'count', 'and', 'or', 'not', 'remoteExec', 'remoteExecCall']

monaco.languages.register({ id: 'sqf', extensions: ['.sqf', '.fsm'] })
monaco.languages.setLanguageConfiguration('sqf', {
  comments: { lineComment: '//', blockComment: ['/*', '*/'] },
  brackets: [['{', '}'], ['[', ']'], ['(', ')']],
  autoClosingPairs: [{ open: '{', close: '}' }, { open: '[', close: ']' }, { open: '(', close: ')' }, { open: '"', close: '"' }, { open: "'", close: "'" }],
})
monaco.languages.setMonarchTokensProvider('sqf', {
  ignoreCase: true,
  keywords: sqfKeywords,
  tokenizer: {
    root: [
      [/\/\/.*$/, 'comment'], [/\/\*/, 'comment', '@comment'], [/^\s*#\s*\w+/, 'keyword.directive'],
      [/"/, 'string', '@dq'], [/'/, 'string', '@sq'],
      [/\b_[A-Za-z0-9_]+/, 'variable'],
      [/\b0x[0-9a-fA-F]+\b|\b\d+(\.\d+)?([eE][-+]?\d+)?\b/, 'number'],
      [/[A-Za-z_]\w*/, { cases: { '@keywords': 'keyword', '@default': 'identifier' } }],
      [/[{}()[\]]/, '@brackets'], [/[;,]/, 'delimiter'], [/[=><!?:&|+\-*/^%#]+/, 'operator'],
    ],
    comment: [[/[^/*]+/, 'comment'], [/\*\//, 'comment', '@pop'], [/[/*]/, 'comment']],
    dq: [[/[^"]+/, 'string'], [/""/, 'string.escape'], [/"/, 'string', '@pop']],
    sq: [[/[^']+/, 'string'], [/''/, 'string.escape'], [/'/, 'string', '@pop']],
  },
})

// mission.sqm, description.ext and other config text (Eden's "a" \n "b" join included).
monaco.languages.register({ id: 'armaconfig', extensions: ['.sqm', '.ext', '.hpp', '.cpp', '.cfg', '.h', '.inc', '.bikey'] })
monaco.languages.setLanguageConfiguration('armaconfig', {
  comments: { lineComment: '//', blockComment: ['/*', '*/'] },
  brackets: [['{', '}'], ['[', ']']],
  autoClosingPairs: [{ open: '{', close: '}' }, { open: '[', close: ']' }, { open: '"', close: '"' }],
})
monaco.languages.setMonarchTokensProvider('armaconfig', {
  keywords: ['class', 'delete'],
  tokenizer: {
    root: [
      [/\/\/.*$/, 'comment'], [/\/\*/, 'comment', '@comment'], [/^\s*#\s*\w+.*$/, 'keyword.directive'],
      [/"/, 'string', '@dq'], [/\\n/, 'string.escape'],
      [/-?\d+(\.\d+)?([eE][-+]?\d+)?\b/, 'number'],
      [/[A-Za-z_]\w*/, { cases: { '@keywords': 'keyword', '@default': 'identifier' } }],
      [/[{}[\]]/, '@brackets'], [/[;:,]/, 'delimiter'], [/\+?=/, 'operator'],
    ],
    comment: [[/[^/*]+/, 'comment'], [/\*\//, 'comment', '@pop'], [/[/*]/, 'comment']],
    dq: [[/[^"]+/, 'string'], [/""/, 'string.escape'], [/"/, 'string', '@pop']],
  },
})

monaco.editor.defineTheme('console', {
  base: 'vs-dark', inherit: true,
  rules: [
    { token: 'keyword', foreground: 'E5584A' }, { token: 'keyword.directive', foreground: 'C586C0' }, { token: 'variable', foreground: '9CDCFE' },
    { token: 'string', foreground: 'CE9178' }, { token: 'string.escape', foreground: 'D7BA7D' }, { token: 'number', foreground: 'B5CEA8' },
    { token: 'comment', foreground: '6A9955', fontStyle: 'italic' },
  ],
  colors: { 'editor.background': '#171A19', 'editorGutter.background': '#171A19', 'editor.lineHighlightBackground': '#1D2120' },
})

const byExtension: Record<string, string> = {
  sqf: 'sqf', fsm: 'sqf', sqm: 'armaconfig', ext: 'armaconfig', hpp: 'armaconfig', cpp: 'armaconfig', cfg: 'armaconfig', h: 'armaconfig', inc: 'armaconfig',
  json: 'json', xml: 'xml', html: 'html', md: 'markdown', txt: 'plaintext', stringtable: 'xml',
}
export const languageOf = (path: string) => byExtension[path.split('.').pop()?.toLowerCase() ?? ''] ?? 'plaintext'

const options: monaco.editor.IStandaloneEditorConstructionOptions = {
  fontFamily: '"IBM Plex Mono", ui-monospace, monospace', fontSize: 13, minimap: { enabled: false }, scrollBeyondLastLine: false,
  automaticLayout: true, tabSize: 4, insertSpaces: false, detectIndentation: true, renderWhitespace: 'selection', wordWrap: 'off',
}

export function CodeEditor({ path, value, readOnly, onChange, onSave, line, height = '100%' }: {
  path: string; value: string; readOnly: boolean; onChange: (v: string) => void; onSave: () => void; line?: number; height?: number | string
}) {
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null)
  const save = useRef(onSave)
  save.current = onSave
  useEffect(() => {
    if (!line || !editor.current) return
    editor.current.revealLineInCenter(line)
    editor.current.setSelection(new monaco.Selection(line, 1, line, editor.current.getModel()?.getLineMaxColumn(line) ?? 1))
    editor.current.focus()
  }, [line, path])
  return (
    <Editor
      path={path} language={languageOf(path)} theme="console" value={value} height={height}
      options={{ ...options, readOnly, domReadOnly: readOnly }}
      onChange={v => onChange(v ?? '')}
      onMount={e => {
        editor.current = e
        e.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => save.current())
        if (line) { e.revealLineInCenter(line); e.setPosition({ lineNumber: line, column: 1 }) }
      }}
    />
  )
}

// The library disposes its models before resetting the diff widget on unmount, which Monaco reports as an error; we keep them,
// then detach and dispose them ourselves.
export function CodeDiff({ path, original, modified, height = '100%' }: { path: string; original: string; modified: string; height?: number | string }) {
  const editor = useRef<monaco.editor.IStandaloneDiffEditor | null>(null)
  useEffect(() => () => {
    const e = editor.current
    const models = e?.getModel()
    try { e?.setModel(null) } catch { /* already disposed */ }
    models?.original.dispose()
    models?.modified.dispose()
  }, [])
  return (
    <DiffEditor language={languageOf(path)} theme="console" original={original} modified={modified} height={height}
      keepCurrentOriginalModel keepCurrentModifiedModel onMount={e => { editor.current = e }}
      options={{ ...options, readOnly: true, renderSideBySide: true, useInlineViewWhenSpaceIsLimited: true }} />
  )
}
