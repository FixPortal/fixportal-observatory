# Contributing

Thanks for your interest in improving this project. It is maintained on a
best-effort basis; issues and pull requests are welcome.

## Ground rules

- Be civil. This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).
- By contributing, you agree your contributions are licensed under the
  [Apache License 2.0](LICENSE), the same licence as the project.
- Open an issue before a large change so we can agree the approach before you
  invest the time.

## Getting set up

Prerequisites: **.NET 10 SDK**, **Node `^22.22.2`, `^24.15.0`, or `>=26.0.0`**, and **PostgreSQL 16** (or Docker).
See the [README](README.md#local-development) for the full local setup,
environment variables, and EF Core migration steps.

```bash
git clone https://github.com/FixPortal/fixportal-observatory.git
cd fixportal-observatory
docker compose up --build
```

This is the shortest complete setup and includes PostgreSQL, migrations, sample
data, the API, ingest worker, and frontend. To run the backend or frontend
directly while developing, follow the README's manual-run commands.

## Before you open a PR

Format locally, then run the full check. CI verifies formatting with
`dotnet csharpier check .` and must pass before a merge:

```powershell
dotnet tool restore
```

```powershell
dotnet csharpier format .
```

```powershell
dotnet format AiObservatory.slnx analyzers --verify-no-changes --no-restore
```

```powershell
dotnet build AiObservatory.slnx --configuration Release --no-restore
```

```powershell
dotnet test --solution AiObservatory.slnx --configuration Release --no-build
```

```powershell
cd src\AiObservatory.Web
```

```powershell
npm run lint
```

```powershell
npm test -- --coverage
```

```powershell
npm run build
```

```powershell
npm run doctor
```

The database-backed .NET tests need Docker: they self-start a PostgreSQL
Testcontainer when `TEST_DB_CONNECTION` is unset, or use that variable's
instance when it is set.

From the repository root, check the complete Docker quick start with Node and
Docker Compose 2.24.4 or newer. This builds the documented stack in a disposable
project, checks seeded API data and worker health, and removes its own containers
and database volume afterwards. It ignores local provider credentials and uses a
random loopback port so it can run alongside your development stack.
The same check runs weekly and on manual dispatch in **Quick-start qualification**;
the full container build stays outside the per-PR test budget.
See the [qualification record](docs/oss-qualification.md) for measured results and
the live-provider checks that remain unverified.

```powershell
node scripts/compose-smoke.mjs
```

The policy checker regression suites also run in CI:

```powershell
python -m pip install pytest
```

```powershell
python -m pytest .github/scripts -q
```

## Branches and commits

- Branch from `main` using `feat/<scope>`, `fix/<scope>`, or `chore/<scope>`.
- Write clear, present-tense commit subjects.
- PRs merge via **rebase** — no merge commits, no squash. Keep your branch
  rebased on `main`.

## What makes a good PR

- One focused change per PR.
- Tests for new behaviour or a bug fix that would have caught the regression.
- No new runtime dependency unless a few lines of code genuinely cannot do it.
- Database schema changes ship with an EF Core migration in the same PR.
