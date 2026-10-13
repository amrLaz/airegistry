# 05 — Règles métier (à transformer en tests d'abord)

## Nommage
- Namespace : 2 à 32 caractères ; nom : 2 à 48 ; minuscules ASCII, chiffres, tirets simples ; forme `namespace-name` ≤ 64.
- Refusés : noms réservés (`synced` pour un skill ; `default`, `worker`, `explorer` pour un agent), suffixes `-gh`, `-azd`, `-feed`, et à la création un nom contenant son type (`skill`, `agent`…).
- Nom hors norme : refus avec forme canonique proposée.
- Noms proches (même type) : refusé dans le même namespace (caractères ambigus normalisés, 1 insertion/suppression/substitution/transposition). Entre namespaces : reste Pending avec constat `name-confusion`.

## Secrets
Secret en clair (jeton, clé API, clé privée, mot de passe assigné) : refus de la publication interne, en nommant détecteur et ligne ; quarantaine sur import de feed.
Détecteurs signalés : URLs réseau privé, chemins propres à une machine, tunnels temporaires, ports de dev, endpoints de recette, références à fichiers d'identifiants. `local-loopback-url` est bloquant ; les détecteurs en observation ne bloquent pas. Chaque constat : détecteur + `fichier:ligne`.

## Validation structurelle par type
- **Skill** : `SKILL.md` à la racine, frontmatter `name` + `description` non vides. Avertissements : name > 64, description > 1024, README.md absent.
- **McpServer** : `mcp.json`/`package.json` JSON valide avec command+args (stdio), url (http/sse) ou docker. Recommandés : name, description, args.
- **Agent** : `.agent.md` ou `AGENTS.md`, nom + description ; sans frontmatter, titre H1 + 1er paragraphe (avertissement). `model`, `tools` recommandés. SKILL.md non exigé.
- **Prompt** : `.prompt.md`, frontmatter `agent` + `description`.
- **Plugin** : manifeste JSON dans le dossier runtime, `name` valide ; composants et chemins contrôlés ; description/version manquantes = avertissement. Chaque entrée `x-myregistry-components` : type, slug, version SemVer exacte existante ; sinon refus (interne) ou quarantaine (externe).
- **Hook** : `hooks.json` avec propriété racine `hooks` (≥ 1 événement, tableau) ; `README.md` avec frontmatter `name`, `description`, `hooks`, `tags`.

## Détection du transport MCP (priorité)
1. `transport` = http/sse/streamable-http ou champ `url` → HTTP/SSE
2. champ `docker` / image → Docker
3. commande contenant `npx` → npx
4. `uvx`/`uv` → uvx
5. sinon stdio

## Versions
- SemVer proposée doit dépasser la dernière version connue de la famille, sinon patch auto-incrémenté. Doublon exact non-SemVer refusé.
- Feed : cascade `version` | `semver` | `release` du frontmatter, sinon `0.0.N` (N augmente seulement si le contenu change).

## Score qualité
A : aucun problème ; B : avertissements mineurs (métadonnées, pas de tags) ; C : ≥ 3 avertissements. Le grade ne remplace pas le scan de sécurité.

## Quarantaine et signalements
≥ 5 signalements en 1 h → quarantaine auto, retrait des résultats publics. Motifs : Injection de prompt, Fuite de données, Contenu inapproprié, Artefact cassé. Un nouveau scan ne lève pas la quarantaine.

## Sécurité (scan)
- Scan direct statique obligatoire avant publication interne (si activé) ; Failed/Unavailable → refus avec cause sûre + référence.
- High/Critical de la dernière analyse réussie (version + empreinte exactes) bloquent l'approbation, même forcée. Low/Medium visibles, non bloquants.
- Finding tardif High/Critical sur version approuvée → `ToReview`. Scan propre → de nouveau approuvable, sans republication auto.
- Dérogation : Moderator/SecurityValidator/Admin ; Critical réservé SecurityValidator/Admin ; motif obligatoire ; liée à (version, content_hash) ; nouvelle version/contenu/finding plus grave rebloque ; révocation → ToReview si findings actifs ; tout tracé dans l'historique.
- Baseline : exclusion par identifiant exact de règle + motif ; confirmation supplémentaire si règle High/Critical.

## Suppression de versions
La dernière version est protégée ; la seule version restante ne peut pas être supprimée.

## Feeds
Pause automatique après plusieurs échecs consécutifs ; artefact absent du dépôt → Deprecated (réapprouvé s'il revient) ; suppression d'un feed supprime ses artefacts importés ; le feed système est non supprimable ; namespace par défaut dérivé avec suffixe fournisseur (`-gh`, `-azd`), changement avec aperçu puis application.
