import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import ProjectPicker from './ProjectPicker';

const options = [
  { key: 'Up.France.CodeInsights', name: 'CodeInsights' },
  { key: 'Up.France.StoreLocator', name: 'Store Locator' },
];

describe('ProjectPicker', () => {
  it('filters, selects one value, marks it without a radio, and closes', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    function SinglePicker() {
      const [value, setValue] = useState('');
      return (
        <ProjectPicker
          label="Project"
          options={options}
          value={value}
          onChange={(nextValue) => {
            onChange(nextValue);
            setValue(nextValue);
          }}
        />
      );
    }

    render(<SinglePicker />);
    const trigger = screen.getByRole('button', { name: /Project Select a project/i });
    await user.click(trigger);
    const search = screen.getByRole('searchbox', { name: 'Search projects' });
    expect(search).toHaveFocus();
    await user.type(search, 'storelocator');

    expect(screen.queryByRole('option', { name: /CodeInsights/ })).not.toBeInTheDocument();
    const option = screen.getByRole('option', { name: 'Store Locator' });
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    await user.click(option);

    expect(onChange).toHaveBeenCalledOnce();
    expect(onChange).toHaveBeenCalledWith('Up.France.StoreLocator');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    expect(trigger).toHaveTextContent('Store Locator');

    await user.click(trigger);
    const selected = screen.getByRole('option', { name: 'Store Locator' });
    expect(selected).toHaveAttribute('aria-selected', 'true');
    expect(within(selected).getByText('✓')).toHaveClass('project-picker-check');
    expect(screen.getByRole('status').textContent).toBe('');
    await user.type(screen.getByRole('searchbox', { name: 'Search projects' }), 'missing');
    expect(screen.getByText('No project matches this search.')).toBeInTheDocument();
  });

  it('supports Arrow keys, Enter, Escape, and restores focus to the trigger', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    render(
      <ProjectPicker
        label="Branch"
        ariaLabel="Branches"
        options={[
          { key: 'main', name: 'main' },
          { key: 'refs/heads/feat/claude-setup-notice', name: 'feat/claude-setup-notice' },
        ]}
        value="main"
        onChange={onChange}
        searchLabel="Search branches"
        searchPlaceholder="Branch name"
        hideOptionKey
      />,
    );

    const trigger = screen.getByRole('button', { name: /Branch main/i });
    await user.click(trigger);
    const search = screen.getByRole('searchbox', { name: 'Search branches' });
    await user.tab();
    expect(screen.getByRole('option', { name: 'main' })).toHaveFocus();
    await user.tab({ shift: true });
    expect(search).toHaveFocus();
    await user.keyboard('{ArrowUp}');
    expect(screen.getByRole('option', { name: 'feat/claude-setup-notice' })).toHaveFocus();
    await user.keyboard('{ArrowDown}');
    expect(screen.getByRole('option', { name: 'main' })).toHaveFocus();
    await user.tab({ shift: true });
    expect(search).toHaveFocus();
    await user.type(search, 'claude');
    await user.keyboard('{ArrowDown}{Enter}');
    expect(onChange).toHaveBeenCalledWith('refs/heads/feat/claude-setup-notice');
    await waitFor(() => expect(trigger).toHaveFocus());

    await user.click(trigger);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
    await waitFor(() => expect(trigger).toHaveFocus());
  });

  it('closes on an outside pointer interaction', async () => {
    const user = userEvent.setup();
    render(
      <>
        <ProjectPicker label="Project" options={options} value="" onChange={() => {}} />
        <button type="button">Outside</button>
      </>,
    );
    await user.click(screen.getByRole('button', { name: /Project Select a project/i }));
    expect(screen.getByRole('listbox')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Outside' }));
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument();
  });

  it('toggles a project favorite without selecting or closing the project row', async () => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const onToggleFavorite = vi.fn();
    render(
      <ProjectPicker label="Project" options={options.map((option) => ({ ...option, isFavorite: false }))} value="Up.France.CodeInsights" onChange={onChange} onToggleFavorite={onToggleFavorite} />,
    );
    await user.click(screen.getByRole('button', { name: /Project CodeInsights/i }));
    await user.click(screen.getByRole('button', { name: 'Add Store Locator to favorites' }));
    expect(onToggleFavorite).toHaveBeenCalledWith('Up.France.StoreLocator');
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole('group', { name: 'Project' })).toBeInTheDocument();
    const selectedProject = screen.getByRole('button', { name: 'CodeInsights' });
    expect(selectedProject).toHaveAttribute('aria-pressed', 'true');
    expect(within(selectedProject).getByText('✓')).toHaveClass('project-picker-check');
  });

  it('offers the empty value only when clearing is meaningful', async () => {
    const user = userEvent.setup();
    const { unmount } = render(<ProjectPicker label="Project" options={options} value="Up.France.CodeInsights" onChange={() => {}} />);
    await user.click(screen.getByRole('button', { name: /Project CodeInsights/i }));
    expect(screen.queryByRole('option', { name: 'Select a project' })).not.toBeInTheDocument();
    unmount();

    render(<ProjectPicker label="Scope" options={options} value="" clearable placeholder="All projects" onChange={() => {}} />);
    await user.click(screen.getByRole('button', { name: /Scope All projects/i }));
    expect(screen.getByRole('option', { name: 'All projects' })).toHaveAttribute('aria-selected', 'true');
  });

  it('renders explicit loading, empty, and error states', async () => {
    const user = userEvent.setup();
    const { rerender } = render(<ProjectPicker label="Branch" options={[]} value="" onChange={() => {}} loading loadingText="Loading branches…" />);
    expect(screen.getByRole('button', { name: /Branch Loading branches/i })).toBeDisabled();

    rerender(
      <ProjectPicker
        label="Branch"
        options={[]}
        value=""
        onChange={() => {}}
        placeholder="No branches available"
        emptyResultsMessage="No branches are available for this project."
        error="Branches could not be loaded."
      />,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('Branches could not be loaded.');
    await user.click(screen.getByRole('button', { name: /Branch No branches available/i }));
    expect(screen.getByText('No branches are available for this project.')).toBeInTheDocument();
  });

  it('reports the search to its caller and shows where that search stands instead of a false no-match', async () => {
    const user = userEvent.setup();
    const onSearchChange = vi.fn();
    const props = { label: 'Branch', options: [{ key: 'refs/heads/develop', name: 'develop' }], value: '', onChange: () => {}, onSearchChange, emptyResultsMessage: 'No branch matches this search.' };
    const { rerender } = render(<ProjectPicker {...props} searchStatus="Searching all branches…" />);
    await user.click(screen.getByRole('button', { name: /Branch/ }));
    await user.type(screen.getByRole('searchbox'), 'master');

    expect(onSearchChange).toHaveBeenLastCalledWith('master');
    expect(screen.getByRole('status')).toHaveTextContent('Searching all branches…');
    expect(screen.queryByText('No branch matches this search.')).not.toBeInTheDocument();

    rerender(<ProjectPicker {...props} />);
    expect(screen.getByText('No branch matches this search.')).toBeInTheDocument();
    await user.keyboard('{Escape}');
    // Closing clears the search, so the caller drops results the list no longer filters.
    expect(onSearchChange).toHaveBeenLastCalledWith('');
  });

  it('supports multiple project selection without losing earlier choices', async () => {
    const user = userEvent.setup();
    function MultiplePicker() {
      const [value, setValue] = useState<string[]>([]);
      return <ProjectPicker multiple ariaLabel="Projects" options={options} value={value} onChange={setValue} />;
    }

    render(<MultiplePicker />);
    await user.click(screen.getByRole('button', { name: /Projects: Select projects/i }));
    await user.click(screen.getByText('CodeInsights', { selector: 'strong' }));
    await user.click(screen.getByText('Store Locator', { selector: 'strong' }));
    expect(screen.getByRole('status')).toHaveTextContent('2 projects selected');
    expect(screen.getByRole('checkbox', { name: /CodeInsights/ })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Store Locator/ })).toBeChecked();
  });

  it('shows group headings without adding them to the accessible options', async () => {
    const user = userEvent.setup();
    render(
      <ProjectPicker
        label="Branch"
        ariaLabel="Branches"
        options={[
          { key: 'develop', name: 'develop', group: 'Branches' },
          { key: 'pr:51382', name: 'PR #51382 · develop → master', group: 'Pull requests' },
          { key: 'pr:7', name: 'PR #7 · feature/x → develop', group: 'Pull requests' },
        ]}
        value="pr:51382"
        onChange={vi.fn()}
        hideOptionKey
      />,
    );
    await user.click(screen.getByRole('button', { name: /Branch/ }));

    const listbox = screen.getByRole('listbox', { name: 'Branches' });
    expect(within(listbox).getAllByRole('option').map((option) => option.getAttribute('aria-label'))).toEqual(['develop', 'PR #51382 · develop → master', 'PR #7 · feature/x → develop']);
    const headings = listbox.querySelectorAll('.project-picker-group');
    expect([...headings].map((heading) => [heading.textContent, heading.getAttribute('aria-hidden')])).toEqual([
      ['Branches', 'true'],
      ['Pull requests', 'true'],
    ]);
    expect(within(listbox).getByRole('option', { name: 'PR #51382 · develop → master' })).toHaveAttribute('aria-selected', 'true');
  });
});
