import { forwardRef, type TextareaHTMLAttributes } from 'react';

export type TextAreaProps = TextareaHTMLAttributes<HTMLTextAreaElement>;

const TextArea = forwardRef<HTMLTextAreaElement, TextAreaProps>(function TextArea({ className = '', ...props }, ref) {
  return <textarea ref={ref} className={`ui-text-area ${className}`.trim()} {...props} />;
});

export default TextArea;
