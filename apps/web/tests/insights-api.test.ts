import { beforeAll, describe, expect, it } from "vitest";

type Page<T> = { body: T; next: string | null; link: string | null };
type Project = {
  project_key: string;
  branch_count: number;
  total_count: number;
  branches: unknown[];
  branches_truncated: boolean;
};
type Branch = { name: string };
type Analysis = { analysis_id: string };

const apiUrl = process.env.CODEINSIGHTS_TEST_URL;
const token = process.env.CODEINSIGHTS_TEST_TOKEN;

describe.skipIf(!apiUrl || !token)("isolated insights API", () => {
  const base = new URL(apiUrl ?? "http://127.0.0.1:50618");
  const headers = { Authorization: `Bearer ${token}` };

  beforeAll(() => {
    expect(["127.0.0.1", "localhost", "[::1]"]).toContain(base.hostname);
  });

  async function page<T>(path: string): Promise<Page<T>> {
    const response = await fetch(new URL(`/api/v1/${path}`, base), { headers });
    expect(response.status, path).toBe(200);
    return {
      body: await response.json() as T,
      next: response.headers.get("X-Next-Cursor"),
      link: response.headers.get("Link")
    };
  }

  it("bounds and advances collection pages", async () => {
    for (const path of ["insights/analyses", "ingestions", "profiles"]) {
      const first = await page<unknown[]>(`${path}?limit=1`);
      expect(Array.isArray(first.body)).toBe(true);
      expect(first.body.length).toBeLessThanOrEqual(1);
      if (first.next) {
        expect(first.link).toContain('rel="next"');
        const second = await page<unknown[]>(`${path}?limit=1&cursor=${encodeURIComponent(first.next)}`);
        expect(second.body.length).toBeLessThanOrEqual(1);
        expect(second.body).not.toEqual(first.body);
      }
    }

    const summary = await page<{ projects: Project[] }>("insights/summary?limit=1");
    expect(summary.body.projects.length).toBeLessThanOrEqual(1);
    if (summary.next) {
      const next = await page<{ projects: Project[] }>(`insights/summary?limit=1&cursor=${encodeURIComponent(summary.next)}`);
      expect(next.body.projects[0]?.project_key).not.toBe(summary.body.projects[0]?.project_key);
    }
    if (summary.body.projects[0]) {
      const first = await page<Branch[]>(`insights/branches?project_key=${encodeURIComponent(summary.body.projects[0].project_key)}&limit=1`);
      expect(first.body.length).toBeLessThanOrEqual(1);
      if (first.next) {
        const next = await page<Branch[]>(`insights/branches?project_key=${encodeURIComponent(summary.body.projects[0].project_key)}&limit=1&cursor=${encodeURIComponent(first.next)}`);
        expect(first.body[0]?.name).not.toBe(next.body[0]?.name);
      }
    }
  });

  it("reads analysis details and rules", async () => {
    const analyses = await page<Analysis[]>("insights/analyses?limit=1");
    expect(analyses.body.length, "Seed at least one isolated analysis").toBeGreaterThan(0);
    for (const kind of ["findings", "components", "comparison"])
      await page(`insights/analyses/${encodeURIComponent(analyses.body[0]!.analysis_id)}/${kind}`);
    await page("insights/rules?limit=1");
  });

  it("keeps the 101-branch fixture bounded when present", async () => {
    const fixture = await page<{ projects: Project[] }>("insights/summary?project_key=check-branches");
    if (!fixture.body.projects.length) return;
    expect((await page<null>("profiles/projects/check-branches")).body).toBeNull();
    const project = fixture.body.projects[0]!;
    expect(project.branch_count).toBe(101);
    expect(project.total_count).toBe(101);
    expect(project.branches).toHaveLength(0);
    expect(project.branches_truncated).toBe(true);
    const names = new Set<string>();
    const lengths: number[] = [];
    let cursor = "";
    do {
      const current = await page<Branch[]>(`insights/branches?project_key=check-branches&cursor=${encodeURIComponent(cursor)}`);
      lengths.push(current.body.length);
      for (const branch of current.body) {
        expect(names.has(branch.name)).toBe(false);
        names.add(branch.name);
      }
      cursor = current.next ?? "";
    } while (cursor);
    expect(lengths).toEqual([50, 50, 1]);
    expect(names.size).toBe(101);
    const selected = await page<Branch[]>("insights/branches?project_key=check-branches&branch=refs%2Fheads%2Fcheck-101");
    expect(selected.body[0]?.name).toBe("refs/heads/check-101");
  });

  it.each(["limit=101", "cursor=bad", "from=bad", "scope=new-code"])("rejects invalid analysis query %s", async query => {
    const response = await fetch(new URL(`/api/v1/insights/analyses?${query}`, base), { headers });
    expect(response.status).toBe(422);
  });
});
