import { forwardRef, type InputHTMLAttributes } from 'react';

export type TextBoxProps = InputHTMLAttributes<HTMLInputElement>;

const TextBox = forwardRef<HTMLInputElement, TextBoxProps>(function TextBox({ className = '', ...props }, ref) {
  return <input ref={ref} className={`ui-text-box ${className}`.trim()} {...props} />;
});

export default TextBox;
