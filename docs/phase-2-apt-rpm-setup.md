# Phase 2 — APT/RPM repo one-time setup runbook

This document is the **checklist for the operator** standing up the signed APT
and RPM repositories at `https://squid.solarifyai.com`. After the steps here,
every Squid Tentacle release tag is automatically packaged, signed, and
published — no further human intervention.

## Prerequisites

- Repo admin access (to set secrets and approve the GPG bootstrap run).
- The `gh-pages` branch already exists with the
  [Phase 1 bootstrap commit](https://github.com/SolarifyDev/Squid/tree/gh-pages).
- DNS: `squid.solarifyai.com` is a CNAME to `solarifydev.github.io`.
- GitHub Pages is enabled, source = `gh-pages` branch, custom domain
  `squid.solarifyai.com`, **Enforce HTTPS** on.

## Step 1 — Generate the GPG signing key (~3 min)

1. Go to **Actions → Bootstrap GPG signing key**.
2. Click **Run workflow**. Inputs:
   - `confirm` → `YES`
   - `name` → `Squid Tentacle Package Signing`
   - `email` → `squid-tentacle@solarifyai.com`
   - `expire` → `2y`
3. Wait for the run to finish (~15 s). Open its log.
4. **Immediately** copy the block between
   `----- BEGIN SQUID_GPG_PRIVATE_KEY -----`
   and `----- END SQUID_GPG_PRIVATE_KEY -----`.
5. Go to **Settings → Secrets and variables → Actions → New repository secret**.
   - Name: `SQUID_GPG_PRIVATE_KEY`
   - Value: paste the entire block including the BEGIN/END lines.
   - **Save**.
6. In the same run log, copy the `----- BEGIN public.key -----` block.
   - Either commit it to `deploy/packaging/public.key` on `main`
     (via a PR, for supply-chain transparency), or just trust that the
     publish workflow re-exports it to `squid.solarifyai.com/public.key`
     on the next release.
7. **Delete the run log**: Actions → Bootstrap GPG signing key → the run →
   **⋯** → **Delete workflow run**. This wipes the private-key material
   from GitHub's log store.

### Why no passphrase?

The key is used only from CI; there is no human at a prompt to unlock it.
A passphrase stored in a second secret adds breakage surface
(passphrase drift vs key drift, extra `--pinentry-mode` plumbing) without
meaningfully increasing security. GitHub Secrets are encrypted at rest and
not exposed in logs by default. The operator surface is effectively
equivalent to a password-less key in AWS Secrets Manager, which is the
industry norm for CI signing keys.

### Rotating the key later

Re-run **Bootstrap GPG signing key**, replace the `SQUID_GPG_PRIVATE_KEY`
secret, and trigger **Build & publish Linux packages** (manual dispatch)
to re-sign the latest release. Old packages stay valid under the old key
(GPG lets clients trust multiple signing keys) until users pull the new
public key.

## Step 2 — First release (~no-op on your part)

Once the secret is set, any **GitHub Release publish** triggers
`.github/workflows/publish-linux-packages.yml`:

1. Downloads the Linux tarball already attached by
   `build-publish-linux-tentacle.yml`.
2. Runs `fpm` to produce `.deb` (amd64 + arm64) and `.rpm` (x86_64 + aarch64).
3. Signs each with the imported GPG key.
4. Attaches the signed packages to the same release (for direct download).
5. Updates the `gh-pages` APT repo via `reprepro` — adds packages to the
   pool, regenerates `Packages`, `Release`, `InRelease`, signs.
6. Updates the RPM repo via `createrepo_c` + signs `repomd.xml`.
7. Commits and pushes `gh-pages`; GitHub Pages auto-deploys to
   `squid.solarifyai.com` within seconds.

To trigger the first publish against an already-published release
(e.g. `v1.3.6`):

- **Actions → Build & publish Linux packages → Run workflow**
- Input: `tag` → `v1.3.6`

## Step 3 — Verify from a fresh machine (~1 min)

Pick any Docker container or clean VM:

```bash
# Ubuntu/Debian
docker run --rm -it ubuntu:22.04 bash -c '
  apt-get update -qq && apt-get install -y -qq curl gnupg ca-certificates
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://squid.solarifyai.com/public.key | gpg --dearmor -o /etc/apt/keyrings/squid.gpg
  echo "deb [signed-by=/etc/apt/keyrings/squid.gpg] https://squid.solarifyai.com/apt stable main" > /etc/apt/sources.list.d/squid.list
  apt-get update && apt-get install -y squid-tentacle
  squid-tentacle --version
'

# RHEL / CentOS / Fedora
docker run --rm -it rockylinux:9 bash -c '
  curl -fsSL https://squid.solarifyai.com/rpm/squid-tentacle.repo -o /etc/yum.repos.d/squid-tentacle.repo
  dnf install -y squid-tentacle
  squid-tentacle --version
'
```

Both should end with a version string printed. If either fails:

- **Signature error** (`NO_PUBKEY` / `Failed to verify signatures`) — the
  pubkey on the clean machine is outdated, or `publish-linux-packages.yml`
  didn't re-export `public.key` (unlikely — it does every run).
- **Package not found** — `publish-linux-packages.yml` didn't actually run.
  Check Actions.
- **Missing dependency** (`libicu not found`) — update the `--depends`
  list in `publish-linux-packages.yml` for the affected distro.

## Step 4 — Wire into `install-tentacle.sh` (future commit)

Once the repo is verified, open a follow-up PR that changes
`deploy/scripts/install-tentacle.sh` to configure the APT/RPM repo
on fresh installs and use `apt-get install` / `yum install` in preference
to `curl | tar`. This is tracked as the **Phase 2 — server-side upgrade
method dispatch** follow-up.

Until that PR lands, users already benefit from the repo today by running
the install commands in Step 3 manually. `install-tentacle.sh` continues
to work via tarball fallback.

## Nightly verification

`.github/workflows/verify-linux-packages.yml` runs at 03:15 UTC daily
against `ubuntu:22.04`, `ubuntu:24.04`, `debian:12`, `rockylinux:9`, and
`fedora:40`. If a distro pushes a breaking change (e.g. libicu major bump
without a compat package), the workflow will fail and notify via GitHub's
default Actions failure emails. Fix by updating `--depends` and releasing
a patch version.

## Security notes

- The signing key material only lives in GitHub Secrets + the imported
  copy inside a workflow runner (destroyed when the runner terminates).
- The published public key (`squid.solarifyai.com/public.key`) is what
  clients trust; it matches the private key's fingerprint. Users can verify
  out-of-band:
  ```bash
  curl -fsSL https://squid.solarifyai.com/public.key | gpg --show-keys
  ```
  and compare the fingerprint against the one recorded at Step 1.
- APT's `signed-by=` option (used in all examples above) pins the repo
  to this specific key; if the key is ever compromised and replaced,
  clients error loudly rather than silently trusting the new key.

## File layout summary

```
main branch
├── .github/workflows/
│   ├── bootstrap-gpg-key.yml              # one-shot, run once
│   ├── publish-linux-packages.yml         # on release published
│   └── verify-linux-packages.yml          # nightly + post-publish
├── deploy/packaging/
│   ├── after-install.sh                   # dpkg/rpm post-install hook
│   ├── before-uninstall.sh                # dpkg/rpm pre-remove hook
│   └── squid-tentacle.repo                # yum .repo descriptor, shipped at /rpm/
└── docs/
    └── phase-2-apt-rpm-setup.md           # this file

gh-pages branch (maintained by CI, never commit manually)
├── index.html
├── public.key                              # GPG public signing key
├── install.sh                              # mirror of install-tentacle.sh
├── apt/
│   ├── conf/                               # reprepro config
│   ├── db/                                 # reprepro state (gitignored)
│   ├── dists/stable/                       # Release, InRelease, Packages.gz
│   └── pool/main/s/squid-tentacle/         # .deb files
└── rpm/
    ├── repodata/                           # repomd.xml + primary.xml.gz
    ├── squid-tentacle.repo                 # yum descriptor (served at /rpm/squid-tentacle.repo)
    └── squid-tentacle-*.rpm
```
