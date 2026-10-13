import * as vscode from 'vscode';

/**
 * Optional chat participant. It is isolated here because the chat API is proprietary to
 * VS Code: a future Cursor or VS Codium build simply does not register the participant,
 * and every other feature keeps working.
 */
export type PromptSource = () => Promise<{ prompt: string; label: string } | undefined>;

export function registerParticipant(context: vscode.ExtensionContext, promptSource: PromptSource): void {
  const chat = (vscode as { chat?: { createChatParticipant?: unknown } }).chat;
  if (typeof chat?.createChatParticipant !== 'function') return;
  const participant = vscode.chat.createChatParticipant('codeinsights.assistant', async (request, _chatContext, stream, token) => {
    const selected = await promptSource();
    if (selected === undefined) {
      stream.markdown(
        "Sélectionner une issue dans la vue **CodeInsights › Issues**, puis relancer. Le contexte vient du hub, ou du fichier local si le hub n'a pas conservé la source analysée.",
      );
      return;
    }
    stream.progress(`Contexte : ${selected.label}`);
    const messages = [
      vscode.LanguageModelChatMessage.User(selected.prompt),
      vscode.LanguageModelChatMessage.User(request.prompt.length === 0 ? 'Explique le problème puis propose un correctif minimal.' : request.prompt),
    ];
    const response = await request.model.sendRequest(messages, {}, token);
    for await (const fragment of response.text) stream.markdown(fragment);
  });
  participant.iconPath = new vscode.ThemeIcon('shield');
  context.subscriptions.push(participant);
}
