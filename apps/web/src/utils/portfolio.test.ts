import { describe, expect, it } from 'vitest';
import { portfolioChart, portfolioDelta, type PortfolioDay } from './portfolio';

const ratings = { a: 2, b: 1, c: 0, d: 0, e: 1, not_rated: 1 };
const day = (value: string, patch: Partial<PortfolioDay> = {}): PortfolioDay => ({
  day: value,
  projects: 5,
  ratings: { security: ratings, reliability: ratings, maintainability: ratings, security_review: ratings },
  findings: { projects: 3, blocker: 1, high: 4, medium: 0, low: 2, info: 0, unknown: 1 },
  sla: null,
  coverage_projects: 4,
  duplication_projects: 4,
  ...patch,
});

describe('portfolioChart', () => {
  it('counts projects per rating letter and keeps the day count as denominator', () => {
    const { rows, series } = portfolioChart([day('2026-09-28')], 'ratings', 'security');
    expect(series.map((item) => item.label)).toEqual(['A', 'B', 'C', 'D', 'E', 'Not rated']);
    expect(rows[0]).toMatchObject({ a: 2, e: 1, not_rated: 1, measured: 5 });
  });

  it('breaks the line on a day without measured projects instead of plotting zero', () => {
    const { rows, measuredDays } = portfolioChart([day('2026-09-27'), day('2026-09-28', { findings: null }), day('2026-09-29')], 'findings');
    expect(rows[1]).toMatchObject({ blocker: null, high: null, measured: null });
    expect(rows[0]).toMatchObject({ blocker: 1, high: 4, measured: 3 });
    expect(measuredDays).toBe(2);
  });

  it('reports no measured day when SLA was never observed over the period', () => {
    expect(portfolioChart([day('2026-09-28'), day('2026-09-29')], 'sla').measuredDays).toBe(0);
    const observed = portfolioChart([day('2026-09-29', { sla: { projects: 2, breached: 3, due_soon: 1 } })], 'sla');
    expect(observed.rows[0]).toMatchObject({ breached: 3, due_soon: 1, measured: 2 });
  });
});

it('portfolioDelta is unknown when either end was not measured', () => {
  expect(portfolioDelta(70, 82)).toBe(12);
  expect(portfolioDelta(null, 82)).toBeNull();
  expect(portfolioDelta(70, null)).toBeNull();
});
