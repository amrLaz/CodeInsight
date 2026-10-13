import { forwardRef, type SelectHTMLAttributes } from 'react';

export type ListBoxProps = SelectHTMLAttributes<HTMLSelectElement>;

const ListBox = forwardRef<HTMLSelectElement, ListBoxProps>(function ListBox({ className = '', children, ...props }, ref) {
  return (
    <select ref={ref} className={`ui-list-box ${className}`.trim()} {...props}>
      {children}
    </select>
  );
});

export default ListBox;
