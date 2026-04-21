# Squid Package Repository (gh-pages)

This branch is the **publishing source** for `https://squid.solarifyai.com` —
the signed APT and RPM package repositories for
[Squid Tentacle](https://github.com/SolarifyDev/Squid).

## ⚠️ Do not commit here manually

This branch is maintained entirely by GitHub Actions workflows
(`.github/workflows/publish-apt-rpm-repo.yml` on the `main` branch).
Manual commits risk breaking signed APT metadata.

## Layout

```
/
├── index.html              ← landing page (human-facing)
├── apt/                    ← Debian/Ubuntu APT repo (signed)
│   ├── dists/stable/       ← metadata: Packages, Release, InRelease
│   └── pool/main/s/        ← .deb files
├── rpm/                    ← RHEL/CentOS/Fedora RPM repo (signed)
│   ├── repodata/           ← metadata: repomd.xml, primary.xml.gz
│   ├── *.rpm
│   └── squid-tentacle.repo ← yum .repo descriptor for users
├── public.key              ← GPG public signing key (ASCII-armoured)
├── install.sh              ← tarball-based one-liner installer
└── CNAME                   ← GitHub Pages custom domain (auto-managed)
```

## How packages get here

1. Tag push (`v1.4.0`) triggers `.github/workflows/build-linux-packages.yml`
   on `main` → builds signed `.deb` and `.rpm`, attaches to GitHub Release.
2. Release published triggers `.github/workflows/publish-apt-rpm-repo.yml` →
   downloads artefacts, updates APT/RPM metadata, signs with GPG, pushes
   here.
3. GitHub Pages picks up the push and serves at `squid.solarifyai.com`
   within seconds.

## GPG key

Published public key: [`public.key`](./public.key).

Import + verify fingerprint before trusting:
```bash
curl -fsSL https://squid.solarifyai.com/public.key | gpg --show-keys
```
