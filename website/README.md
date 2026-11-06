# Project website

Static Jekyll site for <https://nefarius.github.io/PS5LM.Companion/>. Source is isolated
from the application and existing Markdown documentation. No JavaScript, analytics,
external fonts, or runtime service is required.

## Preview locally

Install Ruby and Bundler (WSL is recommended on Windows), then from the repository root:

```sh
cd website
bundle install
bundle exec jekyll serve --host 127.0.0.1
```

Open <http://127.0.0.1:4000/PS5LM.Companion/>. For a production build:

```sh
JEKYLL_ENV=production bundle exec jekyll build
```

`github-pages` supplies the same Jekyll dependency set as the Pages build action.
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
