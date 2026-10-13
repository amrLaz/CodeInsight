import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { parseGitleaks, parseSemgrep, type ProducerResult } from './producers';

const run = promisify(execFile);

/**
 * Runs the local producers. Nothing is installed, downloaded or updated on the
 * developer's machine: a tool that is absent is reported as absent. A Docker image is
 * used only when the setting asks for it, pinned exactly like the CI images.
 */

/**
 * Semgrep accepts both local rule files and registry identifiers such as `p/ci`. Only
 * the former can be checked for existence before running.
 */
export function isPathConfig(entry: string): boolean {
  return entry.startsWith('.') || entry.startsWith('/') || entry.endsWith('.json') || entry.endsWith('.yaml') || entry.endsWith('.yml');
}

export type RunnerOptions = {
  rootPath: string;
  files: string[];
  semgrepConfigs: string[];
  useDocker: boolean;
  images: { semgrep: string; gitleaks: string };
  timeoutMs: number;
  log: (message: string) => void;
};

type Command = { file: string; args: string[] };

async function available(file: string): Promise<boolean> {
  try {
    await run(process.platform === 'win32' ? 'where' : 'which', [file], { timeout: 5000 });
    return true;
  } catch {
    return false;
  }
}

async function execute(command: Command, options: RunnerOptions): Promise<{ stdout: string; code: number }> {
  try {
    const { stdout } = await run(command.file, command.args, {
      cwd: options.rootPath,
      timeout: options.timeoutMs,
      maxBuffer: 32 * 1024 * 1024,
      // The producers must not reach the network from a workstation run.
      env: { ...process.env, SEMGREP_SEND_METRICS: 'off', DO_NOT_TRACK: '1' },
    });
    return { stdout, code: 0 };
  } catch (error) {
    const failure = error as { stdout?: string; code?: number; killed?: boolean; message?: string };
    // Both tools exit non-zero when they report findings: that is a result, not a failure.
    if (typeof failure.code === 'number' && failure.stdout !== undefined && failure.stdout.length > 0)
      return { stdout: failure.stdout, code: failure.code };
    throw new Error(failure.killed === true ? 'délai dépassé' : (failure.message ?? 'échec inconnu'));
  }
}

function dockerArgs(image: string, rootPath: string, args: string[]): Command {
  return { file: 'docker', args: ['run', '--rm', '--network', 'none', '-v', `${rootPath}:/src:ro`, '-w', '/src', image, ...args] };
}

export async function runSemgrep(options: RunnerOptions): Promise<ProducerResult> {
  const tool = 'semgrep';
  if (options.semgrepConfigs.length === 0)
    // Message neutre : le cœur est partagé par l'IDE et le serveur MCP, qui nomment
    // chacun le réglage correspondant dans leur propre vocabulaire.
    return { tool, state: 'absent', reason: 'aucune règle semgrep configurée', findings: [] };
  if (options.files.length === 0) return { tool, state: 'measured', reason: undefined, findings: [] };
  const args = ['scan', '--json', '--quiet', '--disable-version-check', '--metrics=off'];
  for (const config of options.semgrepConfigs) args.push('--config', config);
  args.push(...options.files);
  const command = (await available('semgrep'))
    ? { file: 'semgrep', args }
    : options.useDocker
      ? dockerArgs(options.images.semgrep, options.rootPath, ['semgrep', ...args])
      : undefined;
  if (command === undefined) return { tool, state: 'absent', reason: 'binaire semgrep introuvable', findings: [] };
  try {
    const { stdout } = await execute(command, options);
    return { tool, state: 'measured', reason: undefined, findings: parseSemgrep(stdout, options.rootPath) };
  } catch (error) {
    options.log(`semgrep: ${error instanceof Error ? error.message : String(error)}`);
    return { tool, state: 'failed', reason: error instanceof Error ? error.message : 'échec', findings: [] };
  }
}

export async function runGitleaks(options: RunnerOptions): Promise<ProducerResult> {
  const tool = 'gitleaks';
  if (options.files.length === 0) return { tool, state: 'measured', reason: undefined, findings: [] };
  // `--no-git` scans the working tree as it is, which is exactly what is being reviewed.
  const args = ['detect', '--no-git', '--no-banner', '--redact', '--report-format', 'json', '--report-path', '-', '--source', '.'];
  const command = (await available('gitleaks'))
    ? { file: 'gitleaks', args }
    : options.useDocker
      ? dockerArgs(options.images.gitleaks, options.rootPath, args)
      : undefined;
  if (command === undefined) return { tool, state: 'absent', reason: 'binaire gitleaks introuvable', findings: [] };
  try {
    const { stdout } = await execute(command, options);
    const changed = new Set(options.files.map((file) => file.replace(/^\.\//, '')));
    return {
      tool,
      state: 'measured',
      reason: undefined,
      findings: parseGitleaks(stdout, options.rootPath).filter((finding) => changed.has(finding.path)),
    };
  } catch (error) {
    options.log(`gitleaks: ${error instanceof Error ? error.message : String(error)}`);
    return { tool, state: 'failed', reason: error instanceof Error ? error.message : 'échec', findings: [] };
  }
}
