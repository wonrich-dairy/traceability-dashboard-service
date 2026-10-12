# Processing event contracts (copy)

`ProcessingEvents.cs` is an unchanged copy of Processing Service's
`src/ProcessingService/Domain/Events/ProcessingEvents.cs` at commit
`1c171ec3dfdb0fe739ea4151415f00895b42f2f2` (2026-09-24, SCRUM-68), the version on Processing's
`develop` when it was copied.

Processing has not published its contracts as a package yet: its `docs/kafka-events.md` says the
file "simulates shared library under SCRUM-58". The namespace is kept as
`ProcessingService.Domain.Events`, so when a package is published, replace this folder with a
`PackageReference` and nothing else changes.

Do not edit the copy. If Processing changes the contract, copy the new version over it and update
the commit above.
