import { Button } from '../components/ui';
import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

type ToastTone = 'success' | 'info' | 'error';
type ToastInput = { message: string; tone?: ToastTone; duration?: number };
type ToastRecord = Required<ToastInput> & { id: number };

const ToastContext = createContext<((toast: ToastInput) => void) | null>(null);

export function ToastProvider({ children }: { children: ReactNode }) {
  const nextId = useRef(0);
  const [toasts, setToasts] = useState<ToastRecord[]>([]);
  const dismiss = useCallback((id: number) => setToasts((current) => current.filter((toast) => toast.id !== id)), []);
  const notify = useCallback((toast: ToastInput) => {
    const record: ToastRecord = { id: ++nextId.current, message: toast.message, tone: toast.tone ?? 'success', duration: toast.duration ?? 5000 };
    setToasts((current) => [...current.slice(-2), record]);
  }, []);

  return (
    <ToastContext.Provider value={notify}>
      {children}
      <div className="toast-viewport" aria-live="polite" aria-atomic="false">
        {toasts.map((toast) => (
          <ToastItem key={toast.id} toast={toast} onDismiss={dismiss} />
        ))}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast() {
  const notify = useContext(ToastContext);
  if (!notify) throw new Error('useToast must be used within ToastProvider.');
  return notify;
}

function ToastItem({ toast, onDismiss }: { toast: ToastRecord; onDismiss: (id: number) => void }) {
  useEffect(() => {
    const timeout = window.setTimeout(() => onDismiss(toast.id), toast.duration);
    return () => window.clearTimeout(timeout);
  }, [onDismiss, toast.duration, toast.id]);

  return (
    <div className={`toast ${toast.tone}`} role={toast.tone === 'error' ? 'alert' : 'status'}>
      <span className="toast-mark" aria-hidden="true">
        {toast.tone === 'success' ? '✓' : toast.tone === 'error' ? '!' : 'i'}
      </span>
      <span className="toast-message">{toast.message}</span>
      <Button type="button" className="toast-dismiss" aria-label="Dismiss notification" title="Dismiss" onClick={() => onDismiss(toast.id)}>
        ×
      </Button>
    </div>
  );
}
