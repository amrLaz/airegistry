# Décisions et hypothèses
(Claude Code y consigne chaque hypothèse prise en cas d'ambiguïté : date, décision, raison.)

## 2026-10-13 — Phase 0
- **.NET 10 (LTS)** : conforme à CLAUDE.md. Les packages OpenAPI natifs (`AddOpenApi`) exigent net9+.
- **`/health` = liveness sans dépendance** ; `/health/ready` vérifie la base (utilisé en readiness par ACA plus tard).
- **Config** : `AddEnvironmentVariables("MYREGISTRY_")` ; `MYREGISTRY_DB_CONNECTION` → clé `DB_CONNECTION`.
- **Migrations appliquées au démarrage** (`Seeder`), sauf environnement `Testing`. À revoir en phase 7 (multi-réplicas : advisory lock).
- **Titre et namespace par défaut** : stockés dans la table `settings`, valeurs initiales dans `SettingDefaults` (Application), jamais lus en dur par l'UI.
- **i18n FR/EN** : service `Localizer` maison (dictionnaire) pour la phase 0 ; passage aux `.resx` si le volume de textes augmente.
- **Tests d'intégration phase 0** : `WebApplicationFactory` sans base (env `Testing`). Testcontainers PostgreSQL arrive avec le modèle de données (phase 1).
