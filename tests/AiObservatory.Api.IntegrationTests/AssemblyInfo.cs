// WebApplicationFactory<Program> hosts are built via HostFactoryResolver's process-wide
// DiagnosticListener interception. Running multiple factories' StartServer() calls
// concurrently (the collection-fixture WAF tests vs. the ad-hoc per-test factories in
// DevSeedEndpointTests/StartupGuardsTests) raced and cross-applied ConfigureWebHost
// config between unrelated host builds — observed as a spurious "DB_CONNECTION
// configuration is missing" on factories that DID set it. Serializing the whole
// assembly avoids the race; this is a known WebApplicationFactory constraint, not a bug
// in the harness itself.
//
// This attribute lived in AiObservatory.Api.Tests until the 2026-07-29 split and moved here
// with the factories. It is load-bearing, and more so now: an assembly of nothing but
// WebApplicationFactory tests constructs far more factories concurrently, and the race
// resurfaced immediately as parallel MigrateAsync calls on one database
// ("42P07: relation already exists", "42704: index does not exist"). It does NOT belong
// back in the unit project — nothing there builds a host, and serialising that assembly
// would slow the lane Stryker re-runs on every mutant.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
