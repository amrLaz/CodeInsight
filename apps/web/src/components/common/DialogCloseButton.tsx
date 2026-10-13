import { Button } from '../ui';
export default function DialogCloseButton({ onClick, disabled = false }: { onClick: () => void; disabled?: boolean }) {
  return (
    <Button type="button" className="icon-button" aria-label="Close" title="Close" onClick={onClick} disabled={disabled}>
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M6 6l12 12M18 6 6 18" />
      </svg>
    </Button>
  );
}
