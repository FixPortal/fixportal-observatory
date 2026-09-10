// Ingest is an aliased reference (see this project's .csproj): both it and the API expose a
// public global-namespace Program, so an unaliased reference makes bare `Program` ambiguous
// and breaks every WebApplicationFactory test in this assembly.
extern alias ingest;
using AiObservatory.Api.Services;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using AwesomeAssertions;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace AiObservatory.Api.Tests;

public class ArchitectureTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(BudgetAlertService).Assembly, // anchor: update if BudgetAlertService is renamed/moved
            typeof(AiObservatoryDbContext).Assembly,
            typeof(ingest::AiObservatory.Ingest.ProviderPollingWorkerService).Assembly
        )
        .Build();

    private static readonly IObjectProvider<IType> ApiTypes = Types()
        .That()
        .ResideInNamespace("AiObservatory.Api")
        .Or()
        .ResideInNamespace("AiObservatory.Api.Endpoints")
        .Or()
        .ResideInNamespace("AiObservatory.Api.Services")
        .Or()
        .ResideInNamespace("AiObservatory.Api.Services.GitHub")
        .Or()
        .ResideInNamespace("AiObservatory.Api.Services.Intelligence")
        .As("Api types");

    private static readonly IObjectProvider<IType> IngestTypes = Types()
        .That()
        .ResideInNamespace("AiObservatory.Ingest")
        .Or()
        .ResideInNamespace("AiObservatory.Ingest.Services.Anthropic")
        .Or()
        .ResideInNamespace("AiObservatory.Ingest.Services.Copilot")
        .Or()
        .ResideInNamespace("AiObservatory.Ingest.Services.Google")
        .Or()
        .ResideInNamespace("AiObservatory.Ingest.Services.OpenAi")
        .As("Ingest types");

    [Fact]
    public void Interfaces_must_have_I_prefix()
    {
        Interfaces().Should().HaveNameStartingWith("I").Check(Architecture);
    }

    [Fact]
    public void Model_types_must_be_sealed()
    {
        Classes().That().ResideInNamespace("AiObservatory.Data.Entities").Should().BeSealed().Check(Architecture);
    }

    [Fact]
    public void Api_must_not_depend_on_Ingest()
    {
        Types().That().Are(ApiTypes).Should().NotDependOnAny(IngestTypes).Check(Architecture);
    }

    [Fact]
    public void Ingest_must_not_depend_on_Api()
    {
        Types().That().Are(IngestTypes).Should().NotDependOnAny(ApiTypes).Check(Architecture);
    }

    // The ledger deliberately holds no link back to a bank, card, invoice or
    // counterparty — that is the privacy boundary that let billed spend live in a
    // public repo at all (spec §3). A convention would erode; this makes it fail
    // the build. If a future feature genuinely needs one of these, that is a
    // design decision to reopen in the spec, not a test to relax.
    [Fact]
    public void SpendEntry_must_not_carry_bank_linkage()
    {
        var forbidden = new[] { "account", "card", "counterparty", "iban", "sortcode", "transactionid" };

        var offenders = typeof(SpendEntry)
            .GetProperties()
            .Where(p => forbidden.Any(f => p.Name.Replace("_", "").Contains(f, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToArray();

        offenders.Should().BeEmpty("SpendEntry must not tie spend to a bank, card, invoice or counterparty (spec §3)");
    }

    // This project is the one Stryker mutates against, so every test here is re-run against
    // every mutant. A test that boots a host or opens a database connection therefore costs
    // the mutation run enormously — which is exactly what happened while the exclusion was
    // a test filter Stryker's MTP runner silently ignored. Exclusion is now structural (the
    // database-backed tests live in AiObservatory.Api.IntegrationTests), and this asserts
    // the structure rather than trusting it.
    [Fact]
    public void Unit_test_project_must_not_reference_database_or_host_packages()
    {
        // Microsoft.AspNetCore.TestHost is deliberately NOT here: a bare in-memory pipeline
        // (ForwardedHeadersConfigTests) is a genuine unit test. It is Mvc.Testing —
        // WebApplicationFactory, which boots the real composition root — and the database
        // packages that mark a test as belonging in the integration project.
        //
        // Microsoft.EntityFrameworkCore.InMemory is deliberately NOT here either, for the same
        // reason as TestHost: it is in-process with no server, no container and nothing to
        // connect to, so it costs a mutant microseconds. It is what lets the money paths that
        // take a DbContext (GitHubBillingSyncService) be mutated at all. What this rule is
        // really protecting is "no external service, no host boot" — a provider that opens a
        // connection (Npgsql) or a fixture that starts one (Testcontainers) still belongs in
        // the integration project, and so does anything asserting on a check constraint or a
        // unique index, which this provider does not enforce.
        var forbidden = new[] { "Npgsql", "Testcontainers", "Microsoft.AspNetCore.Mvc.Testing" };

        var offenders = typeof(ArchitectureTests)
            .Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => forbidden.Any(f => n.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToArray();

        offenders
            .Should()
            .BeEmpty(
                "this project is Stryker's test lane; host-booting and database-backed tests belong in "
                    + "AiObservatory.Api.IntegrationTests, or every mutant pays for them (docs/mutation-testing.md)"
            );
    }
}
