import { CliError, type ArtifactType } from './types.js';

export interface CatalogItem {
  id: string; namespace: string; name: string; type: ArtifactType; version: string; description?: string;
  status: string; grade: string; contentHash: string; votes: number; downloads: number; tags: string[];
}
export interface ByName { id: string; namespace: string; name: string; type: ArtifactType; version: string; contentHash: string }
export interface Issue { code: string; message: string; file?: string; line?: number }

export class Api {
  constructor(private baseUrl: string, private token?: string, private fetchImpl: typeof fetch = fetch) {}

  private async request(pathAndQuery: string, init: RequestInit = {}): Promise<Response> {
    const headers: Record<string, string> = { ...(init.headers as Record<string, string>) };
    if (this.token) headers.Authorization = `Bearer ${this.token}`;
    try {
      return await this.fetchImpl(this.baseUrl + pathAndQuery, { ...init, headers });
    } catch {
      throw new CliError(`Registre injoignable : ${this.baseUrl} (vérifiez MYREGISTRY_BASE_URL ou « myregistry config »).`);
    }
  }

  async list(q: { type?: string; search?: string; tag?: string; page?: number; pageSize?: number }) {
    const p = new URLSearchParams();
    if (q.type) p.set('type', q.type);
    if (q.search) p.set('q', q.search);
    if (q.tag) p.set('tag', q.tag);
    p.set('page', String(q.page ?? 1));
    p.set('pageSize', String(Math.min(q.pageSize ?? 20, 100)));
    const res = await this.request(`/api/catalog?${p}`);
    if (!res.ok) throw new CliError(`Échec de la liste (${res.status}).`);
    return (await res.json()) as { items: CatalogItem[]; total: number; page: number; pageSize: number };
  }

  async byName(ns: string, name: string, version?: string): Promise<ByName | undefined> {
    const res = await this.request(`/api/catalog/by-name/${encodeURIComponent(ns)}/${encodeURIComponent(name)}${version ? `?version=${encodeURIComponent(version)}` : ''}`);
    if (res.status === 404) return undefined;
    if (!res.ok) throw new CliError(`Échec de la résolution de ${ns}/${name} (${res.status}).`);
    return (await res.json()) as ByName;
  }

  async download(id: string): Promise<{ data: Uint8Array; sha256: string | null }> {
    const res = await this.request(`/api/catalog/${id}/download`);
    if (!res.ok) throw new CliError(`Échec du téléchargement (${res.status}).`);
    return { data: new Uint8Array(await res.arrayBuffer()), sha256: res.headers.get('x-artifact-sha256') };
  }

  async registryInfo(): Promise<{ title?: string; defaultNamespace?: string; marketplaceRepo?: string | null; publicUrl?: string | null }> {
    const res = await this.request('/api/registry-info');
    if (!res.ok) throw new CliError(`registry-info indisponible (${res.status}).`);
    return (await res.json()) as { title?: string; defaultNamespace?: string; marketplaceRepo?: string | null; publicUrl?: string | null };
  }

  async validate(body: unknown) {
    const res = await this.request('/api/validate', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
    if (!res.ok) throw new CliError(`Validation impossible (${res.status}).`);
    return (await res.json()) as { isValid: boolean; detectedType?: ArtifactType; errors: Issue[]; warnings: Issue[]; grade: string };
  }

  async suggestedVersion(ns: string, name: string, type: string, requested?: string): Promise<string> {
    const p = new URLSearchParams({ ns, name, type });
    if (requested) p.set('requestedVersion', requested);
    const res = await this.request(`/api/cli/publish/suggested-version?${p}`);
    if (!res.ok) throw new CliError(`Version suggérée indisponible (${res.status}).`);
    return ((await res.json()) as { version: string }).version;
  }

  async importArtifact(body: unknown): Promise<{ ok: boolean; result: any }> {
    const res = await this.request('/api/import', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
    const result = await res.json().catch(() => ({}));
    return { ok: res.ok, result };
  }
}
