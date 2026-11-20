export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly field?: string // set on "validation" errors that belong to one form field

  constructor(status: number, code: string, message: string, field?: string) {
    super(message)
    this.status = status
    this.code = code
    this.field = field
  }
}

const unsafe = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

const fallbackMessage: Record<number, string> = {
  401: 'You need to sign in.',
  403: "You don't have access to this.",
  404: 'Not found.',
  429: 'Too many attempts. Wait a minute and try again.',
}

export async function api<T>(path: string, init: RequestInit & { json?: unknown } = {}): Promise<T> {
  const { json, headers, ...rest } = init
  const method = (rest.method ?? (json === undefined ? 'GET' : 'POST')).toUpperCase()
  const h = new Headers(headers)
  if (json !== undefined) h.set('Content-Type', 'application/json')
  if (unsafe.has(method)) h.set('X-Console', '1')

  const res = await fetch(path, {
    ...rest,
    method,
    headers: h,
    credentials: 'same-origin',
    body: json === undefined ? rest.body : JSON.stringify(json),
  })

  const text = res.status === 204 ? '' : await res.text()
  let data: unknown
  try { data = text ? JSON.parse(text) : undefined } catch { data = undefined }

  if (!res.ok) {
    const body = (data ?? {}) as { code?: string; message?: string; field?: string }
    throw new ApiError(res.status, body.code ?? `http_${res.status}`, body.message ?? fallbackMessage[res.status] ?? 'Something went wrong. Try again.', body.field)
  }
  return data as T
}

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : 'Something went wrong. Try again.'
}

// Only same-site relative paths, so ?next= can't bounce a member to another site.
export function safeNext(next: string | null): string {
  return next && next.startsWith('/') && !next.startsWith('//') ? next : '/'
}
