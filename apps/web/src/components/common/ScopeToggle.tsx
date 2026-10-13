import { Button } from '../ui';
import type { ScopeMode } from '@/types/api';

// Sized to the analysis metadata line it closes, so switching scope never grows that line.
export default function ScopeToggle({ value, onChange }: { value: ScopeMode; onChange: (scope: ScopeMode) => void }) {
  return (
    <div className="scope-toggle" role="group" aria-label="Analysis scope">
      <Button type="button" aria-pressed={value === 'overall'} className={value === 'overall' ? 'active' : ''} onClick={() => onChange('overall')}>
        Overall
      </Button>
      <Button type="button" aria-pressed={value === 'new-code'} className={value === 'new-code' ? 'active' : ''} onClick={() => onChange('new-code')}>
        New Code
      </Button>
    </div>
  );
}
