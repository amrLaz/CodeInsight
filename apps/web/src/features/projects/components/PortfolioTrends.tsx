import { API_BASE } from '@/config/env';
import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { CartesianGrid, Line, LineChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import { apiJson } from '@/services/api';
import { Button, Grid, ListBox, type GridColumn } from '@/components/ui';
import { portfolioChart, portfolioDelta, type PortfolioProject, type PortfolioResponse, type RatingDimension, type TrendSeries } from '@/utils/portfolio';

type View = 'ratings' | 'findings' | 'sla' | 'quality';
const VIEWS: ReadonlyArray<{ id: View; label: string }> = [
  { id: 'ratings', label: 'Ratings' },
  { id: 'findings', label: 'Findings' },
  { id: 'sla', label: 'SLA' },
  { id: 'quality', label: 'Coverage & duplication' },
];
const DIMENSIONS: ReadonlyArray<{ id: RatingDimension; label: string }> = [
  { id: 'security', label: 'Security' },
  { id: 'reliability', label: 'Reliability' },
  { id: 'maintainability', label: 'Maintainability' },
  { id: 'security_review', label: 'Security review' },
];
const PERIODS = [30, 90, 180, 365] as const;

function TrendChart({ label, rows, series }: { label: string; rows: ReturnType<typeof portfolioChart>['rows']; series: readonly TrendSeries[] }) {
  const latest = rows[rows.length - 1];
  return (
    <>
      <ul className="metric-history-legend" aria-label={`${label} on the latest day`}>
        {series.map((item) => (
          <li key={item.key}>
            <span className="metric-history-swatch" style={{ borderColor: item.color, borderTopStyle: item.dash ? 'dashed' : 'solid' }} aria-hidden="true" />
            <span>{item.label}</span>
            <strong>{latest?.[item.key] == null ? 'Not measured' : String(latest[item.key])}</strong>
          </li>
        ))}
      </ul>
      <figure className="metric-history-chart" aria-label={`${label} per day`}>
        <ResponsiveContainer width="100%" height="100%">
          <LineChart data={rows} margin={{ top: 12, right: 18, bottom: 12, left: 4 }} accessibilityLayer>
            <CartesianGrid vertical={false} stroke="var(--border)" />
            <XAxis
              dataKey="timestamp"
              type="number"
              scale="time"
              domain={['dataMin', 'dataMax']}
              tickFormatter={(value) => new Date(value).toLocaleDateString()}
              minTickGap={36}
              tick={{ fill: 'var(--text-secondary)', fontSize: 12 }}
              tickLine={{ stroke: 'var(--text-tertiary)' }}
              axisLine={{ stroke: 'var(--border)' }}
            />
            <YAxis allowDecimals={false} width={44} tick={{ fill: 'var(--text-secondary)', fontSize: 12 }} tickLine={false} axisLine={false} />
            <Tooltip
              labelFormatter={(value, payload) => {
                const measured = payload?.[0]?.payload?.measured;
                return `${new Date(Number(value)).toLocaleDateString()}${measured == null ? '' : ` · ${measured} project(s) measured`}`;
              }}
              cursor={{ stroke: 'var(--text-tertiary)', strokeDasharray: '3 3' }}
              contentStyle={{ background: 'var(--bg-lighter)', border: '1px solid var(--border)', borderRadius: '6px', color: 'var(--text-primary)' }}
              labelStyle={{ color: 'var(--text-secondary)' }}
              itemStyle={{ color: 'var(--text-primary)' }}
            />
            {series.map((item) => (
              <Line key={item.key} type="linear" dataKey={item.key} name={item.label} stroke={item.color} strokeDasharray={item.dash} strokeWidth={2} dot={false} activeDot={{ r: 4 }} connectNulls={false} isAnimationActive={false} />
            ))}
          </LineChart>
        </ResponsiveContainer>
      </figure>
    </>
  );
}

const percent = (value: number | null) => (value == null ? 'Not measured' : `${value}%`);
const change = (value: number | null) => (value == null ? '-' : value > 0 ? `+${value} pts` : `${value} pts`);

export default function PortfolioTrends({ authToken }: { authToken?: string }) {
  const [days, setDays] = useState<(typeof PERIODS)[number]>(90);
  const [view, setView] = useState<View>('ratings');
  const [dimension, setDimension] = useState<RatingDimension>('security');
  const query = useQuery({
    queryKey: ['portfolio', days],
    queryFn: ({ signal }) => apiJson<PortfolioResponse>(`${API_BASE}/v1/insights/portfolio?days=${days}`, authToken, { signal }),
  });
  const data = query.data;
  const chart = useMemo(() => (data && view !== 'quality' ? portfolioChart(data.days, view, dimension) : null), [data, view, dimension]);
  const columns = useMemo<GridColumn<PortfolioProject>[]>(
    () => [
      { id: 'project', header: 'Project', value: (row) => row.project_key, width: 260, minWidth: 160, rowHeader: true },
      { id: 'coverage', header: 'Coverage', value: (row) => percent(row.coverage), width: 130, minWidth: 110 },
      { id: 'coverage-change', header: 'Coverage change', value: (row) => change(portfolioDelta(row.coverage_start, row.coverage)), width: 160, minWidth: 130 },
      { id: 'duplication', header: 'Duplication', value: (row) => percent(row.duplication), width: 130, minWidth: 110 },
      { id: 'duplication-change', header: 'Duplication change', value: (row) => change(portfolioDelta(row.duplication_start, row.duplication)), width: 170, minWidth: 140 },
      { id: 'day', header: 'Latest day', value: (row) => new Date(`${row.day}T00:00:00Z`).toLocaleDateString(), width: 140, minWidth: 110 },
    ],
    [],
  );
  // Lowest coverage first: the table exposes the weakest projects instead of averaging them away.
  const projects = useMemo(() => [...(data?.projects ?? [])].sort((left, right) => (left.coverage ?? Infinity) - (right.coverage ?? Infinity) || left.project_key.localeCompare(right.project_key)), [data]);

  return (
    <section className="portfolio-trends" aria-labelledby="portfolio-trends-title">
      <div className="portfolio-trends-header">
        <h3 id="portfolio-trends-title">Portfolio trends</h3>
        <label>
          Period
          <ListBox value={String(days)} onChange={(event) => setDays(Number(event.target.value) as (typeof PERIODS)[number])}>
            {PERIODS.map((period) => (
              <option key={period} value={period}>
                Last {period} days
              </option>
            ))}
          </ListBox>
        </label>
      </div>
      <p className="supporting-copy">Main branch of each authorized project, one point per day. A project that was not measured on a day is left out of that day, never counted as zero.</p>
      {query.isLoading && <p role="status">Loading portfolio trends...</p>}
      {query.error && (
        <p className="feedback error" role="alert">
          {query.error.message}
        </p>
      )}
      {data && (
        <>
          {data.projects_without_main_branch > 0 && (
            <p role="status">
              {data.projects_without_main_branch} project(s) without a main branch are not included.
            </p>
          )}
          {!data.days.length ? (
            <p className="empty-state" role="status">
              No daily snapshot yet: trends appear after the next hourly maintenance run.
            </p>
          ) : (
            <>
              <div className="metric-history-tabs" role="tablist" aria-label="Portfolio trend">
                {VIEWS.map((item) => (
                  <Button key={item.id} type="button" role="tab" id={`portfolio-tab-${item.id}`} aria-controls="portfolio-panel" aria-selected={view === item.id} className={view === item.id ? 'active' : ''} onClick={() => setView(item.id)}>
                    {item.label}
                  </Button>
                ))}
              </div>
              <div className="portfolio-trends-panel" role="tabpanel" id="portfolio-panel" aria-labelledby={`portfolio-tab-${view}`}>
                {view === 'ratings' && (
                  <label>
                    Rating
                    <ListBox value={dimension} onChange={(event) => setDimension(event.target.value as RatingDimension)}>
                      {DIMENSIONS.map((item) => (
                        <option key={item.id} value={item.id}>
                          {item.label}
                        </option>
                      ))}
                    </ListBox>
                  </label>
                )}
                {chart && (chart.measuredDays === 0 ? (
                  <p className="metric-history-status" role="status">
                    {view === 'sla' ? 'SLA counts are recorded from the day this feature runs; earlier days were never observed.' : 'No project was measured over this period.'}
                  </p>
                ) : (
                  <TrendChart label={VIEWS.find((item) => item.id === view)!.label} rows={chart.rows} series={chart.series} />
                ))}
                {view === 'quality' && <Grid ariaLabel="Coverage and duplication per project" caption="Latest value and change over the period, lowest coverage first" rows={projects} columns={columns} rowKey={(row) => row.project_key} />}
              </div>
            </>
          )}
        </>
      )}
    </section>
  );
}
