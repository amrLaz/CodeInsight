import { forwardRef, type ButtonHTMLAttributes } from 'react';

export type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'neutral' | 'primary' | 'danger';
};

const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button({ className = '', variant = 'neutral', type = 'button', ...props }, ref) {
  return <button ref={ref} type={type} className={`ui-button ${variant === 'neutral' ? '' : variant} ${className}`.trim()} {...props} />;
});

export default Button;
