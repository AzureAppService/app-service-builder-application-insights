## Purpose
<!-- Describe the problem and change. -->

## Does this introduce a breaking change?
- [ ] Yes
- [ ] No

## Pull Request Type
- [ ] Bugfix
- [ ] Feature
- [ ] Documentation
- [ ] Dependency update

## How to Test
<!-- Include exact commands and their results. -->

```bash
dotnet restore tests/Sample.Tests.csproj --configfile NuGet.Config --locked-mode
dotnet test tests/Sample.Tests.csproj --no-restore
dotnet publish BuilderApplicationInsightsSample.csproj --no-restore -c Release
```

## What to Check
- [ ] Identity selection, local-authentication boundary and telemetry redaction remain intact.
- [ ] Bounded concurrency, traffic and absolute expiry remain intact.
- [ ] Documentation describes changed behavior.
- [ ] No credentials, private test identifiers or deployment receipts are included.

## Other Information
<!-- Local/mocked tests are not hosted ingestion evidence. -->
