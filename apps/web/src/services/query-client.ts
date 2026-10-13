import { QueryClient } from '@tanstack/react-query';

export function createSessionQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { staleTime: 30_000, retry: false, refetchOnWindowFocus: false, refetchOnReconnect: false },
      mutations: { retry: false },
    },
  });
}
