import { Fragment, useEffect, useId, useMemo, useRef, useState, type KeyboardEvent } from 'react';
import './ProjectPicker.css';
import { Button, TextBox, Checkbox } from '../ui';

export type ProjectPickerOption = {
  key: string;
  name?: string;
  isFavorite?: boolean;
  /** Visual heading shown before the first option of a group; options must already be ordered by group. */
  group?: string;
};

type SharedProps = {
  options: ProjectPickerOption[];
  label?: string;
  ariaLabel?: string;
  placeholder?: string;
  emptySelectionHint?: string;
  disabled?: boolean;
  loading?: boolean;
  loadingText?: string;
  error?: string | null;
  searchLabel?: string;
  searchPlaceholder?: string;
  emptyResultsMessage?: string;
  /** Receives the search text, so the caller can look beyond the options it holds. */
  onSearchChange?: (search: string) => void;
  /** Where the caller's own search stands; replaces the no-match message while set. */
  searchStatus?: string;
  hideOptionKey?: boolean;
  className?: string;
};

type SingleProps = SharedProps & {
  multiple?: false;
  value: string;
  onChange: (value: string) => void;
  /** Offer the empty value as a row only where clearing is a real user action. */
  clearable?: boolean;
  onToggleFavorite?: (key: string) => void;
  favoritePending?: boolean;
};

type MultipleProps = SharedProps & {
  multiple: true;
  value: string[];
  onChange: (value: string[]) => void;
};

export type ProjectPickerProps = SingleProps | MultipleProps;

function filterProjectOptions(options: ProjectPickerOption[], search: string) {
  const query = search.trim().toLocaleLowerCase();
  if (!query) return options;
  return options.filter((option) => `${option.name ?? ''} ${option.key}`.toLocaleLowerCase().includes(query));
}

export default function ProjectPicker(props: ProjectPickerProps) {
  const {
    options,
    label,
    ariaLabel = label ?? 'Projects',
    placeholder,
    emptySelectionHint,
    disabled = false,
    loading = false,
    loadingText = 'Loading projects…',
    error,
    searchLabel = 'Search projects',
    searchPlaceholder = 'Project name or key',
    emptyResultsMessage = 'No project matches this search.',
    onSearchChange,
    searchStatus = '',
    hideOptionKey = false,
    className = '',
  } = props;
  const root = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const searchInput = useRef<HTMLInputElement>(null);
  const [open, setOpen] = useState(false);
  const [search, setSearch] = useState('');
  const id = useId();
  const panelId = `${id}-panel`;
  const filteredOptions = useMemo(() => filterProjectOptions(options, search), [options, search]);
  const selectedKeySet = useMemo(() => new Set(props.multiple ? props.value : props.value ? [props.value] : []), [props.multiple, props.value]);
  const selectedOption = !props.multiple ? options.find((option) => option.key === props.value) : undefined;
  const emptyLabel = placeholder ?? (props.multiple ? 'Select projects' : 'Select a project');
  const summary = props.multiple
    ? selectedKeySet.size > 0
      ? `${selectedKeySet.size} project${selectedKeySet.size === 1 ? '' : 's'} selected`
      : emptyLabel
    : selectedOption?.name || selectedOption?.key || props.value || emptyLabel;
  const unavailable = disabled || loading;
  const selectionStatus = props.multiple
    ? selectedKeySet.size === 0 && emptySelectionHint
      ? emptySelectionHint
      : `${selectedKeySet.size} project${selectedKeySet.size === 1 ? '' : 's'} selected`
    : selectedKeySet.size === 0 && emptySelectionHint
      ? emptySelectionHint
      : '';
  const singleWithActions = !props.multiple && !!props.onToggleFavorite;

  const changeSearch = (value: string) => {
    setSearch(value);
    onSearchChange?.(value);
  };

  const close = (restoreFocus = false) => {
    setOpen(false);
    changeSearch('');
    if (restoreFocus) window.setTimeout(() => trigger.current?.focus(), 0);
  };

  useEffect(() => {
    if (!open) return;
    searchInput.current?.focus();
    const closeOutside = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) close();
    };
    document.addEventListener('pointerdown', closeOutside);
    return () => document.removeEventListener('pointerdown', closeOutside);
  }, [open]);

  const focusOption = (index: number) => {
    const optionButtons = Array.from(root.current?.querySelectorAll<HTMLElement>('[data-picker-option]:not([aria-disabled="true"])') ?? []);
    if (optionButtons.length > 0) optionButtons[(index + optionButtons.length) % optionButtons.length]?.focus();
  };

  const optionKeyDown = (event: KeyboardEvent<HTMLElement>, index: number) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      focusOption(index + (event.key === 'ArrowDown' ? 1 : -1));
    }
  };

  return (
    <div
      ref={root}
      className={`project-picker ${className}`.trim()}
      onBlur={() => {
        window.setTimeout(() => {
          if (open && !root.current?.contains(document.activeElement)) close();
        }, 0);
      }}
      onKeyDownCapture={(event) => {
        if (open && event.key === 'Escape') {
          event.preventDefault();
          close(true);
        }
      }}
    >
      {label && (
        <span id={`${id}-label`} className="project-picker-label">
          {label}
        </span>
      )}
      <div className="project-picker-dropdown">
        <Button
          ref={trigger}
          type="button"
          className="project-picker-trigger"
          aria-expanded={open}
          aria-controls={panelId}
          aria-haspopup={!props.multiple && !singleWithActions ? 'listbox' : undefined}
          aria-labelledby={label ? `${id}-label ${id}-summary` : undefined}
          aria-label={label ? undefined : `${ariaLabel}: ${loading ? loadingText : summary}`}
          disabled={unavailable}
          title={loading ? loadingText : summary}
          onClick={() => setOpen((current) => !current)}
        >
          <span id={`${id}-summary`}>{loading ? loadingText : summary}</span>
          <span className="project-picker-chevron" aria-hidden="true" />
        </Button>
        {open && (
          <div id={panelId} className="project-picker-panel">
            <label className="project-picker-search">
              <span>{searchLabel}</span>
              <TextBox
                ref={searchInput}
                type="search"
                aria-label={searchLabel}
                aria-controls={`${id}-options`}
                value={search}
                onChange={(event) => changeSearch(event.target.value)}
                onKeyDown={(event) => {
                  if (!props.multiple && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
                    event.preventDefault();
                    focusOption(event.key === 'ArrowDown' ? 0 : -1);
                  }
                }}
                placeholder={searchPlaceholder}
              />
            </label>
            <div className="project-picker-status" role="status">
              {[selectionStatus, searchStatus].filter(Boolean).join(' ')}
            </div>
            <div
              id={`${id}-options`}
              className={`project-picker-options ${props.multiple ? 'multiple' : 'single'}`}
              role={props.multiple || singleWithActions ? 'group' : 'listbox'}
              aria-label={ariaLabel}
            >
              {!props.multiple && props.clearable && (
                <Button
                  data-picker-option
                  role={singleWithActions ? undefined : 'option'}
                  aria-selected={singleWithActions ? undefined : !props.value}
                  aria-pressed={singleWithActions ? !props.value : undefined}
                  className={`project-picker-option${!props.value ? ' selected' : ''}`}
                  onClick={() => {
                    props.onChange('');
                    close(true);
                  }}
                  onKeyDown={(event) => optionKeyDown(event, 0)}
                >
                  <span>
                    <strong>{emptyLabel}</strong>
                  </span>
                  <span className="project-picker-check" aria-hidden="true">
                    ✓
                  </span>
                </Button>
              )}
              {filteredOptions.map((option, index) => {
                const checked = selectedKeySet.has(option.key);
                if (!props.multiple) {
                  const favoriteAction = props.onToggleFavorite;
                  // The option label names its kind; the heading only groups them visually.
                  const groupHeading = option.group && option.group !== filteredOptions[index - 1]?.group ? option.group : null;
                  return (
                    <Fragment key={option.key}>
                    {groupHeading && (
                      <div className="project-picker-group" aria-hidden="true">
                        {groupHeading}
                      </div>
                    )}
                    <div className={`project-picker-option-row${checked ? ' selected' : ''}`} role="presentation">
                      <Button
                        data-picker-option
                        role={singleWithActions ? undefined : 'option'}
                        aria-selected={singleWithActions ? undefined : checked}
                        aria-pressed={singleWithActions ? checked : undefined}
                        aria-label={option.name || option.key}
                        className="project-picker-option"
                        title={option.name || option.key}
                        onClick={() => {
                          props.onChange(option.key);
                          close(true);
                        }}
                        onKeyDown={(event) => optionKeyDown(event, index + (props.clearable ? 1 : 0))}
                      >
                        <span>
                          <strong>{option.name || option.key}</strong>
                          {!hideOptionKey && option.name && option.name !== option.key && <small>{option.key}</small>}
                        </span>
                        <span className="project-picker-check" aria-hidden="true">
                          ✓
                        </span>
                      </Button>
                      {favoriteAction && (
                        <Button
                          className="project-picker-favorite"
                          aria-label={`${option.isFavorite ? 'Remove' : 'Add'} ${option.name || option.key} ${option.isFavorite ? 'from' : 'to'} favorites`}
                          aria-pressed={option.isFavorite ?? false}
                          disabled={props.favoritePending}
                          onClick={() => favoriteAction(option.key)}
                        >
                          <span aria-hidden="true">{option.isFavorite ? '★' : '☆'}</span>
                        </Button>
                      )}
                    </div>
                    </Fragment>
                  );
                }
                return (
                  <label className="project-picker-option" key={option.key} title={option.name || option.key}>
                    <Checkbox
                      type="checkbox"
                      className="ui-checkbox"
                      aria-label={option.name && option.name !== option.key ? `${option.name} (${option.key})` : option.key}
                      checked={checked}
                      onChange={() => props.onChange(checked ? props.value.filter((key) => key !== option.key) : [...props.value, option.key])}
                    />
                    <span>
                      <strong>{option.name || option.key}</strong>
                      {!hideOptionKey && option.name && option.name !== option.key && <small>{option.key}</small>}
                    </span>
                  </label>
                );
              })}
              {filteredOptions.length === 0 && !searchStatus && <p className="project-picker-empty">{emptyResultsMessage}</p>}
            </div>
          </div>
        )}
      </div>
      {error && (
        <span className="project-picker-feedback" role="alert">
          {error}
        </span>
      )}
    </div>
  );
}
