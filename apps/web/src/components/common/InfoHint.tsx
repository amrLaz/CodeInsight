import { Button } from '../ui';
import { useId, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';

type TooltipPosition = { left: number; top?: number; bottom?: number };

export default function InfoHint({ children, label = 'Information' }: { children: ReactNode; label?: string }) {
  const id = useId();
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState<TooltipPosition>({ left: 16, top: 16 });
  const anchor = useRef<HTMLSpanElement>(null);
  const show = () => {
    const rect = anchor.current?.getBoundingClientRect();
    if (!rect) return;
    const width = Math.min(360, window.innerWidth * 0.75);
    const left = Math.max(16, Math.min(rect.left, window.innerWidth - width - 16));
    setPosition(window.innerHeight - rect.bottom >= 220 ? { left, top: rect.bottom + 8 } : { left, bottom: window.innerHeight - rect.top + 8 });
    setOpen(true);
  };
  return (
    <span
      ref={anchor}
      className="info-hint"
      onMouseEnter={show}
      onMouseLeave={(event) => {
        if (!event.currentTarget.contains(document.activeElement)) setOpen(false);
      }}
      onFocus={show}
      onBlur={() => setOpen(false)}
      onKeyDown={(event) => {
        if (event.key === 'Escape') {
          setOpen(false);
          event.stopPropagation();
        }
      }}
    >
      <Button type="button" className="info-hint-button" aria-label={label} aria-describedby={open ? id : undefined} aria-expanded={open} onClick={show}>
        i
      </Button>
      {open &&
        createPortal(
          <span id={id} role="tooltip" className="info-hint-content" style={position}>
            {children}
          </span>,
          document.body,
        )}
    </span>
  );
}
