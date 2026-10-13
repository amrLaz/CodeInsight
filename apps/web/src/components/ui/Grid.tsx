import { useId, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent, type PointerEvent, type ReactNode } from 'react';

export type GridColumn<Row> = {
  id: string;
  header: ReactNode;
  label?: string;
  value?: (row: Row) => string | number | null | undefined;
  render?: (row: Row) => ReactNode;
  tooltip?: (row: Row) => string | null | undefined;
  width?: number;
  minWidth?: number;
  className?: string;
  rowHeader?: boolean;
};

export type GridProps<Row> = {
  ariaLabel: string;
  rows: readonly Row[];
  columns: readonly GridColumn<Row>[];
  rowKey: (row: Row) => string;
  caption?: ReactNode;
  className?: string;
  resizable?: boolean;
};

function OverflowText({ children, tooltip }: { children: ReactNode; tooltip: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const [truncated, setTruncated] = useState(false);
  const tooltipId = useId();

  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    const measure = () => setTruncated(element.scrollWidth > element.clientWidth || element.scrollHeight > element.clientHeight);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(element);
    return () => observer.disconnect();
  }, [children]);

  return (
    <div ref={ref} className={`ui-grid-overflow${truncated ? ' truncated' : ''}`} tabIndex={truncated ? 0 : undefined} aria-describedby={truncated ? tooltipId : undefined}>
      <div className="ui-grid-overflow-value">{children}</div>
      {truncated && (
        <span id={tooltipId} className="ui-grid-tooltip" role="tooltip">
          {tooltip}
        </span>
      )}
    </div>
  );
}

export default function Grid<Row>({ ariaLabel, rows, columns, rowKey, caption, className = '', resizable = true }: GridProps<Row>) {
  const table = useRef<HTMLTableElement>(null);
  const initialWidths = useMemo(() => Object.fromEntries(columns.map((column) => [column.id, column.width ?? 180])), [columns]);
  const [widths, setWidths] = useState<Record<string, number>>(initialWidths);

  const resizeWithKeyboard = (event: KeyboardEvent<HTMLButtonElement>, column: GridColumn<Row>) => {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    const delta = event.key === 'ArrowRight' ? 16 : -16;
    setWidths((current) => ({ ...current, [column.id]: Math.max(column.minWidth ?? 80, (current[column.id] ?? column.width ?? 180) + delta) }));
  };

  const startResize = (event: PointerEvent<HTMLButtonElement>, column: GridColumn<Row>) => {
    event.preventDefault();
    const handle = event.currentTarget;
    const startX = event.clientX;
    const startWidth = widths[column.id] ?? column.width ?? 180;
    const minWidth = column.minWidth ?? 80;
    handle.setPointerCapture?.(event.pointerId);

    const move = (moveEvent: globalThis.PointerEvent) => {
      const next = Math.max(minWidth, startWidth + moveEvent.clientX - startX);
      table.current?.style.setProperty(`--ui-grid-${column.id}-width`, `${next}px`);
    };
    const finish = (upEvent: globalThis.PointerEvent) => {
      const next = Math.max(minWidth, startWidth + upEvent.clientX - startX);
      handle.releasePointerCapture?.(event.pointerId);
      handle.removeEventListener('pointermove', move);
      handle.removeEventListener('pointerup', finish);
      handle.removeEventListener('pointercancel', finish);
      setWidths((current) => ({ ...current, [column.id]: next }));
    };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', finish);
    handle.addEventListener('pointercancel', finish);
  };

  const style = Object.fromEntries(columns.map((column) => [`--ui-grid-${column.id}-width`, `${widths[column.id] ?? column.width ?? 180}px`])) as CSSProperties;

  return (
    <div className={`ui-grid-wrap ${className}`.trim()} tabIndex={0} role="region" aria-label={ariaLabel}>
      <table ref={table} className="ui-grid" style={style}>
        {caption && <caption>{caption}</caption>}
        <thead>
          <tr>
            {columns.map((column) => (
              <th key={column.id} scope="col" style={{ width: `var(--ui-grid-${column.id}-width)`, minWidth: column.minWidth }}>
                <span>{column.header}</span>
                {resizable && (
                  <button
                    type="button"
                    className="ui-grid-resizer"
                    aria-label={`Resize ${column.label ?? String(column.header)} column`}
                    title={`Resize ${column.label ?? String(column.header)} column`}
                    onPointerDown={(event) => startResize(event, column)}
                    onKeyDown={(event) => resizeWithKeyboard(event, column)}
                  />
                )}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((row) => (
            <tr key={rowKey(row)}>
              {columns.map((column) => {
                const value = column.value?.(row);
                const content = column.render ? column.render(row) : value;
                const tooltip = column.tooltip?.(row) ?? (typeof value === 'string' || typeof value === 'number' ? String(value) : null);
                const cellContent = tooltip ? <OverflowText tooltip={tooltip}>{content}</OverflowText> : content;
                return column.rowHeader ? (
                  <th key={column.id} scope="row" className={column.className} data-label={column.label ?? (typeof column.header === 'string' ? column.header : undefined)}>
                    {cellContent}
                  </th>
                ) : (
                  <td key={column.id} className={column.className} data-label={column.label ?? (typeof column.header === 'string' ? column.header : undefined)}>
                    {cellContent}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
