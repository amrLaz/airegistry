export type ArtifactType = 'McpServer' | 'Skill' | 'Agent' | 'Prompt' | 'Plugin' | 'Hook';
export type Agent = 'copilot' | 'claude' | 'codex';
export const ARTIFACT_TYPES: ArtifactType[] = ['McpServer', 'Skill', 'Agent', 'Prompt', 'Plugin', 'Hook'];
export const AGENTS: Agent[] = ['copilot', 'claude', 'codex'];

export class CliError extends Error {
  constructor(message: string, public exitCode = 1) {
    super(message);
  }
}
