// File-manager paths are relative to a folder set, separated by backslashes, as the agent resolves them.
export const join = (folder: string, name: string) => (folder ? `${folder}\\${name}` : name)
export const parentOf = (path: string) => (path.includes('\\') ? path.slice(0, path.lastIndexOf('\\')) : '')
export const nameOf = (path: string) => path.slice(path.lastIndexOf('\\') + 1)
export const fileQuery = (install: string, space: string, path: string) => `install=${install}&space=${space}&path=${encodeURIComponent(path)}`
