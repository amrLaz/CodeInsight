export type LicensePolicy = { allowed_licenses: string[]; denied_licenses: string[]; version: number; updated_by: string; updated_at: string };
export type LicenseUsage = { license: string; components: number; projects: number };
export type LicenseInventory = { projects: number; components: number; unlicensed_components: number; unlicensed_projects: number; licenses: LicenseUsage[]; truncated: boolean };

/** One exact licence string per line; blank lines and duplicates are dropped, case is kept. */
export function parseLicenseLines(text: string): string[] {
  return [...new Set(text.split('\n').map((line) => line.trim()).filter(Boolean))];
}

/** Licences listed as both allowed and denied, which the API refuses. */
export function licenseOverlap(allowed: readonly string[], denied: readonly string[]): string[] {
  const deniedSet = new Set(denied);
  return allowed.filter((license) => deniedSet.has(license));
}

/** Appends a licence to a one-per-line list unless it is already there. */
export function appendLicense(text: string, license: string): string {
  const lines = parseLicenseLines(text);
  return lines.includes(license) ? text : [...lines, license].join('\n');
}
