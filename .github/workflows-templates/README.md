# Iskra Sprint 3.5 — CI workflow templates

These YAML files **are not active in this repo**. They live here as templates
to copy into the two new repos that drive auto-discovery:

| File | Goes into | Purpose |
|---|---|---|
| `notify-iskra-catalog.yml` | every `*-firmware` repo, at `.github/workflows/` | On `release.published`, fires a `repository_dispatch` event at `iskra-catalog`. |
| `regenerate-catalog.yml` | `oleksandrmaslov/iskra-catalog`, at `.github/workflows/` | Walks every `*-firmware` repo, collects `target.json` files, runs `Iskra.Cli --generate-catalog`, signs the result, publishes as a new release of `iskra-catalog`. |

## One-time setup

### 1. Create the `iskra-catalog` repo

- New **public** repo: `oleksandrmaslov/iskra-catalog`
- README it however you like; the repo's job is to host signed catalog releases.

### 2. Move the dev signing key into the CI secret

Encode the existing Ed25519 private key (it's already base64 on disk) and store it as a repo secret in `iskra-catalog`:

```powershell
$priv = Get-Content "C:\Users\IMT - Teilnehmer\.claude\projects\c--Users-Alexandr-flashlight-app\keys\catalog-key.priv" -Raw
gh secret set CATALOG_PRIV_KEY --repo oleksandrmaslov/iskra-catalog --body $priv
```

(Requires `gh` CLI authenticated; the secret value is what's already inside the `.priv` file — a 44-character base64 string.)

### 3. Drop `regenerate-catalog.yml` into iskra-catalog

```powershell
# from this repo:
cd c:\Users\Alexandr\iskra-app
# copy the workflow into a working clone of iskra-catalog:
Copy-Item .github\workflows-templates\regenerate-catalog.yml `
          c:\Users\Alexandr\iskra-catalog\.github\workflows\regenerate-catalog.yml
```

Commit + push to `iskra-catalog`. Test the workflow with **Actions tab → Run workflow** (the `workflow_dispatch` trigger is there for exactly this purpose).

### 4. Wire the firmware repos to notify the catalog

For each `*-firmware` repo (starting with `ci-clop-firmware`):

```powershell
Copy-Item .github\workflows-templates\notify-iskra-catalog.yml `
          c:\Users\Alexandr\ci-clop-firmware\.github\workflows\notify-iskra-catalog.yml
```

Create a fine-grained PAT (https://github.com/settings/personal-access-tokens/new) with:
- **Resource owner:** `oleksandrmaslov`
- **Repository access:** `iskra-catalog` only
- **Repository permissions → Actions:** Read and write
- **Expiry:** 1 year

Store as `ISKRA_CATALOG_DISPATCH_TOKEN` secret in the firmware repo:

```powershell
$token = Read-Host -AsSecureString "Paste PAT"
$plain = [System.Net.NetworkCredential]::new("", $token).Password
gh secret set ISKRA_CATALOG_DISPATCH_TOKEN --repo oleksandrmaslov/ci-clop-firmware --body $plain
```

Commit + push the workflow file to the firmware repo. The next `gh release create` in that repo will automatically trigger the catalog regenerate.

## End-to-end smoke test

1. In `ci-clop-firmware`, run the release walkthrough from CLAUDE.md (steps 5–7).
2. Within ~30 seconds, watch the Actions tab of `iskra-catalog` — `regenerate catalog` job should appear and complete.
3. https://github.com/oleksandrmaslov/iskra-catalog/releases shows a new release `catalog-<timestamp>` with `catalog.json` and `catalog.json.sig` assets.
4. Once chunks 3–5 are done, the WPF app on the lab box picks up the new catalog automatically and offers v1.0.0 (or whatever was just released) in the version dropdown.
