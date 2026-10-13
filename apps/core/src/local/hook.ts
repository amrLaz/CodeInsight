/**
 * Pre-push reminder proposed by the IDE. It never blocks a push and is never written
 * without an explicit command: installing a hook by surprise would hide a network
 * effect behind an editor setting.
 */

export const HOOK_MARKER = '# codeinsights-pre-push';

export function prePushHook(commandTitle: string): string {
  return `#!/bin/sh
${HOOK_MARKER}
# Rappel installé depuis VS Code. Il n'empêche pas le push : l'estimation locale
# n'est pas le gate CI, et un crochet Git ne peut pas exécuter l'extension.
echo "CodeInsights : lancer « ${commandTitle} » dans VS Code avant de pousser si les changements n'ont pas été relus."
exit 0
`;
}

export function alreadyInstalled(existing: string | undefined): boolean {
  return existing !== undefined && existing.includes(HOOK_MARKER);
}
