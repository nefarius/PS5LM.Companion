# Project website

Static Jekyll site for <https://nefarius.github.io/PS5LM.Companion/>. Source is isolated
from the application and existing Markdown documentation. No JavaScript, analytics,
external fonts, or runtime service is required.

## Preview locally

The verified environment is Debian 12 Linux (x86_64), with **Ruby 3.3.4** and
**Bundler 2.5.11**, as shipped in the CI image
`ghcr.io/actions/jekyll-build-pages:v1.0.13` (pinned by digest in `pages.yml`). Use these versions to reproduce the
build; on Windows, use Debian in WSL2. The repository does not separately pin a
Ruby or Bundler version. `Gemfile` selects `github-pages ~> 232`; the verified
image contains `github-pages 232` and Jekyll 3.10.0. The tracked `Gemfile.lock`
locks the full dependency set for this Linux x86_64 environment, with additional
variants for Intel/Apple Silicon macOS and ARM64 Linux (glibc) local previews.

For a native install, provide Ruby development headers matching your interpreter,
GCC/G++, and Make (`build-essential` on Debian), plus `pkg-config`, `zlib1g-dev`,
`libffi-dev`, and `patch`. The gem set includes native extensions such as
`commonmarker`, `eventmachine`, and `http_parser.rb`; Nokogiri and ffi may also
need compilation when a prebuilt gem is unavailable. See the
[Jekyll Linux prerequisites](https://jekyllrb.com/docs/installation/ubuntu/).

After installing Ruby 3.3.4 and the build prerequisites, install Bundler and run
these commands from the repository root:

```sh
cd website
gem install bundler -v 2.5.11
BUNDLE_FROZEN=true bundle _2.5.11_ install
bundle _2.5.11_ exec jekyll serve --host 127.0.0.1
```

Open <http://127.0.0.1:4000/PS5LM.Companion/>. For a production build:

```sh
JEKYLL_ENV=production bundle _2.5.11_ exec jekyll build
```

CI uses the digest-pinned image as its Ruby runtime, then runs a frozen
`bundle install --local` and `bundle exec jekyll build` against this repository’s
lockfile. It does not invoke the Pages build action, which otherwise resolves
its own gems independently. A missing or mismatched gem fails the build rather
than silently changing versions. Update the image digest and lockfile together,
and verify the frozen build whenever dependencies change. Native preview installs
fetch the same locked versions from RubyGems.
`_config.yml` holds the repository URL, public origin, and project `baseurl`.
All local links and assets use Jekyll URL filters. Update these values if the
repository is renamed, forked, or moved to a custom domain.

Images in `assets/images` are snapshots of the application logo and the overview,
downloads, and inference screenshots from the repository. Refresh them when the
dashboard screenshots change. Edit content in `index.html`, the shared shell in
`_layouts/default.html`, and presentation in `assets/style.css`.

## Deployment

In repository **Settings → Pages → Build and deployment → Source**, select
**GitHub Actions** once. The `GitHub Pages` workflow builds pull requests without
deploying them and publishes changes under `website/` on `master`. It also supports
manual runs from `master`. The production environment is `github-pages`.

The workflow uses GitHub's Jekyll build and Pages artifact/deployment actions; no
personal token, `gh-pages` branch, or committed build output is needed. Existing
project CI still builds and tests the companion independently.
