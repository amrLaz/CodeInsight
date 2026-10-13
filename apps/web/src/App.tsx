import { useEffect, useRef, useState } from 'react';
import { QueryClientProvider } from '@tanstack/react-query';
import { sessionEvents } from './services/api';
import { createSessionQueryClient } from './services/query-client';
import { ToastProvider } from './context/ToastProvider';
import Workspace from './workspace/Workspace';
import type { SessionUser } from './types/api';

const LOCAL_STORAGE_SESSION_KEY = 'sast-platform-session-v1';

export function App() {
  const [session, setSession] = useState<{ user: SessionUser | null; revision: number }>(() => {
    try {
      const user = JSON.parse(localStorage.getItem(LOCAL_STORAGE_SESSION_KEY) ?? 'null') as SessionUser | null;
      return { user: user?.token ? user : null, revision: 0 };
    } catch {
      return { user: null, revision: 0 };
    }
  });
  const activeToken = useRef(session.user?.token);
  useEffect(() => {
    const expireSession = (event: Event) => {
      // Ignore late responses belonging to a previous identity.
      if ((event as CustomEvent<string>).detail !== activeToken.current) return;
      activeToken.current = undefined;
      localStorage.removeItem(LOCAL_STORAGE_SESSION_KEY);
      setSession((current) => ({ user: null, revision: current.revision + 1 }));
    };
    sessionEvents.addEventListener('expired', expireSession);
    return () => sessionEvents.removeEventListener('expired', expireSession);
  }, []);
  return (
    <SessionApp
      key={session.revision}
      initialUser={session.user}
      onSessionChange={(user) => {
        activeToken.current = user?.token;
        setSession((current) => ({ user, revision: current.revision + 1 }));
      }}
    />
  );
}

function SessionApp(props: { initialUser: SessionUser | null; onSessionChange: (user: SessionUser | null) => void }) {
  // A new identity gets a new component tree and cache, including all form drafts.
  const [client] = useState(createSessionQueryClient);
  useEffect(
    () => () => {
      void client.cancelQueries();
    },
    [client],
  );
  return (
    <QueryClientProvider client={client}>
      <ToastProvider>
        <Workspace {...props} />
      </ToastProvider>
    </QueryClientProvider>
  );
}
