import { Button } from '../components/ui';
import { useLayoutEffect, useRef, useState } from 'react';

export function visibleTabCount(widths: number[], available: number, moreWidth: number, gap: number): number {
  if (widths.reduce((sum, width) => sum + width, 0) + Math.max(0, widths.length - 1) * gap <= available) return widths.length;
  let used = moreWidth;
  let count = 0;
  for (const width of widths) {
    if (used + gap + width > available) break;
    used += gap + width;
    count++;
  }
  return count;
}

export default function MainNavigation<T extends string>({ tabs, activeTab, onSelect }: { tabs: Array<{ id: T; label: string }>; activeTab: T; onSelect: (id: T) => void }) {
  const nav = useRef<HTMLElement>(null);
  const measure = useRef<HTMLDivElement>(null);
  const menu = useRef<HTMLDetailsElement>(null);
  const [visibleCount, setVisibleCount] = useState(tabs.length);
  useLayoutEffect(() => {
    const update = () => {
      if (!nav.current || !measure.current) return;
      const style = getComputedStyle(nav.current);
      const widths = Array.from(measure.current.children, (child) => child.getBoundingClientRect().width);
      const moreWidth = widths.pop() ?? 36;
      setVisibleCount(visibleTabCount(widths, nav.current.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight), moreWidth, parseFloat(style.columnGap) || 0));
    };
    update();
    const observer = new ResizeObserver(update);
    if (nav.current) observer.observe(nav.current);
    if (measure.current) observer.observe(measure.current);
    return () => observer.disconnect();
  }, [tabs]);
  useLayoutEffect(() => {
    const closeOutside = (event: PointerEvent) => {
      if (menu.current && !menu.current.contains(event.target as Node)) menu.current.open = false;
    };
    document.addEventListener('pointerdown', closeOutside);
    return () => document.removeEventListener('pointerdown', closeOutside);
  }, []);
  const hiddenTabs = tabs.slice(visibleCount);
  const hiddenActive = hiddenTabs.find((tab) => tab.id === activeTab);
  const select = (id: T) => {
    if (menu.current?.open) {
      menu.current.open = false;
      menu.current.querySelector('summary')?.focus();
    }
    onSelect(id);
  };
  return (
    <nav ref={nav} className="main-tabs" aria-label="Main tabs">
      <div className="navigation-measure" aria-hidden="true" inert>
        <div ref={measure}>
          {tabs.map((tab) => (
            <Button type="button" key={tab.id} className="main-tab" tabIndex={-1}>
              {tab.label}
            </Button>
          ))}
          <span className="main-tab navigation-more-trigger">⋮</span>
        </div>
      </div>
      {tabs.slice(0, visibleCount).map((tab) => (
        <Button type="button" key={tab.id} className={activeTab === tab.id ? 'main-tab active' : 'main-tab'} aria-current={activeTab === tab.id ? 'page' : undefined} onClick={() => select(tab.id)}>
          {tab.label}
        </Button>
      ))}
      {hiddenTabs.length > 0 && (
        <details
          ref={menu}
          className="navigation-more"
          onToggle={(event) => {
            if (!event.currentTarget.open) return;
            const panel = event.currentTarget.querySelector<HTMLElement>('.navigation-overflow');
            if (!panel) return;
            const x = event.currentTarget.getBoundingClientRect().left;
            panel.style.left = `${Math.max(16 - x, Math.min(0, window.innerWidth - x - panel.offsetWidth - 16))}px`;
            panel.style.right = 'auto';
          }}
          onKeyDown={(event) => {
            if (event.key === 'Escape') {
              event.currentTarget.open = false;
              event.currentTarget.querySelector('summary')?.focus();
              event.preventDefault();
              event.stopPropagation();
            }
            if (!event.currentTarget.open || !['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) return;
            const buttons = Array.from(event.currentTarget.querySelectorAll('button'));
            const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1 : event.key === 'ArrowDown' ? (index + 1) % buttons.length : (index <= 0 ? buttons.length : index) - 1;
            buttons[next]?.focus();
            event.preventDefault();
          }}
        >
          <summary
            className={`main-tab navigation-more-trigger${hiddenActive ? ' active' : ''}`}
            title={hiddenActive ? `Autres vues · ${hiddenActive.label}` : 'Autres vues'}
            aria-label={hiddenActive ? `Autres vues, vue active : ${hiddenActive.label}` : 'Autres vues'}
          >
            ⋮
          </summary>
          <div className="navigation-overflow">
            {hiddenTabs.map((tab) => (
              <Button
                type="button"
                key={tab.id}
                className={activeTab === tab.id ? 'main-tab active' : 'main-tab'}
                aria-current={activeTab === tab.id ? 'page' : undefined}
                onClick={() => select(tab.id)}
              >
                {tab.label}
              </Button>
            ))}
          </div>
        </details>
      )}
    </nav>
  );
}
