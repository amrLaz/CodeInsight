import { forwardRef, type InputHTMLAttributes } from 'react';

export type CheckboxProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'type'> & {
  type?: 'checkbox';
};

const Checkbox = forwardRef<HTMLInputElement, CheckboxProps>(function Checkbox({ className = '', ...props }, ref) {
  return <input ref={ref} type="checkbox" className={`ui-checkbox ${className}`.trim()} {...props} />;
});

export default Checkbox;
