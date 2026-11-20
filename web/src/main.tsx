import { QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from 'react-router'
import { router } from './App'
import { ApiError } from './lib/api'
import './styles/app.css'

const queryClient: QueryClient = new QueryClient({
  // A 401 or "enrol 2FA first" from any query re-checks /api/me, and RequireAuth then routes to login or enrolment.
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (query.queryKey[0] !== 'me' && error instanceof ApiError && (error.status === 401 || error.code === '2fa_enrolment_required'))
        void queryClient.invalidateQueries({ queryKey: ['me'] })
    },
  }),
  defaultOptions: {
    queries: { retry: (count, error) => !(error instanceof ApiError) && count < 2 },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
)
