import type { ExtensionContext } from 'vscode';

/**
 * The personal access token never leaves SecretStorage: settings are synced
 * across machines and appear in logs and support bundles.
 */
const KEY = 'codeinsights.pat';

export class TokenStore {
  constructor(private readonly context: ExtensionContext) {}

  get(): Promise<string | undefined> {
    return Promise.resolve(this.context.secrets.get(KEY));
  }

  async set(token: string): Promise<void> {
    await this.context.secrets.store(KEY, token.trim());
  }

  async clear(): Promise<void> {
    await this.context.secrets.delete(KEY);
  }
}
