import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import Grid, { type GridColumn } from './Grid';

type Row = { id: string; name: string };

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe('Grid', () => {
  it('resizes columns with the keyboard and exposes the complete truncated value', async () => {
    vi.stubGlobal(
      'ResizeObserver',
      class {
        constructor(private readonly callback: ResizeObserverCallback) {}
        observe(target: Element) {
          this.callback([{ target } as ResizeObserverEntry], this as unknown as ResizeObserver);
        }
        disconnect() {}
        unobserve() {}
      },
    );
    vi.spyOn(HTMLElement.prototype, 'scrollWidth', 'get').mockReturnValue(320);
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(120);
    const columns: GridColumn<Row>[] = [{ id: 'name', header: 'Name', rowHeader: true, value: (row) => row.name, width: 180, minWidth: 100 }];
    const longName = 'A complete value that does not fit in the visible column';
    const { container } = render(<Grid ariaLabel="Example results" caption="Example caption" rows={[{ id: '1', name: longName }]} columns={columns} rowKey={(row) => row.id} />);

    expect(screen.getByText('Example caption').tagName).toBe('CAPTION');
    expect(screen.getByRole('rowheader')).toHaveTextContent(longName);
    expect(screen.getByRole('tooltip')).toHaveTextContent(longName);
    const resizer = screen.getByRole('button', { name: 'Resize Name column' });
    resizer.focus();
    await userEvent.setup().keyboard('{ArrowRight}');
    expect(container.querySelector('table')).toHaveStyle({ '--ui-grid-name-width': '196px' });
  });
});
