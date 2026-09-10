using System.Text.Json;
using AiObservatory.Data;
using AiObservatory.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;

namespace AiObservatory.Api.Endpoints;

// Request records are instantiated by ASP.NET Core model binding.
// ReSharper disable ClassNeverInstantiated.Global

/// <summary>
/// Categories and vendors — the user-managed axes of the spend ledger. Kept apart from
/// the ledger endpoints: these change rarely and for different reasons.
/// </summary>
public static class SpendCatalogEndpoints
{
    // ReSharper disable once UnusedMethodReturnValue.Global
    public static IEndpointRouteBuilder MapSpendCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/spend/categories", GetCategoriesAsync);
        app.MapGet("/spend/categories/{id:guid}", GetCategoryByIdAsync).WithName("GetSpendCategoryById");
        app.MapPost("/spend/categories", CreateCategoryAsync);
        app.MapPatch("/spend/categories/{id:guid}", PatchCategoryAsync);

        app.MapGet("/spend/vendors", GetVendorsAsync);
        app.MapGet("/spend/vendors/{id:guid}", GetVendorByIdAsync).WithName("GetSpendVendorById");
        app.MapPost("/spend/vendors", CreateVendorAsync);
        app.MapPatch("/spend/vendors/{id:guid}", PatchVendorAsync);

        return app;
    }

    private static async Task<IResult> GetCategoriesAsync(
        AiObservatoryDbContext db,
        CancellationToken ct,
        bool includeArchived = false
    )
    {
        var q = db.SpendCategories.AsNoTracking();
        if (!includeArchived)
        {
            q = q.Where(c => c.ArchivedAt == null);
        }

        return Results.Ok(await q.OrderBy(c => c.SortOrder).ThenBy(c => c.DisplayName).ToListAsync(ct));
    }

    private static async Task<IResult> GetCategoryByIdAsync(Guid id, AiObservatoryDbContext db, CancellationToken ct)
    {
        var category = await db.SpendCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        return category is not null ? Results.Ok(category) : Results.NotFound();
    }

    private static async Task<IResult> CreateCategoryAsync(
        SpendCategoryRequest req,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        var key = Slug(req.Key);
        if (key is null || ValidateName(req.DisplayName) is not null)
        {
            return Results.BadRequest("Key must be a slug of 60 characters or fewer and DisplayName is required");
        }

        if (ValidateColorVar(req.ColorVar) is { } colorError)
        {
            return Results.BadRequest(colorError);
        }

        if (await db.SpendCategories.AnyAsync(c => c.Key == key, ct))
        {
            return Results.Conflict($"Category key already exists: {key}");
        }

        var category = new SpendCategory
        {
            Key = key,
            DisplayName = req.DisplayName.Trim(),
            ColorVar = req.ColorVar?.Trim() ?? "",
            SortOrder = req.SortOrder,
        };
        db.SpendCategories.Add(category);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two concurrent creates of the same key both pass the AnyAsync pre-check above
            // -- one wins the unique index and this one lands here. Same message as the
            // pre-check, so the two paths are indistinguishable to a caller.
            db.Entry(category).State = EntityState.Detached;
            return Results.Conflict($"Category key already exists: {key}");
        }
        return Results.CreatedAtRoute("GetSpendCategoryById", new { id = category.Id }, category);
    }

    private static async Task<IResult> PatchCategoryAsync(
        Guid id,
        SpendCatalogPatchRequest req,
        AiObservatoryDbContext db,
        IClock clock,
        CancellationToken ct
    )
    {
        var category = await db.SpendCategories.FindAsync([id], ct);
        if (category is null)
        {
            return Results.NotFound();
        }

        if (req.DisplayName is { } name)
        {
            if (ValidateName(name) is { } error)
            {
                return Results.BadRequest(error);
            }
            category.DisplayName = name.Trim();
        }

        if (req.ColorVar is { } color)
        {
            if (ValidateColorVar(color) is { } colorError)
            {
                return Results.BadRequest(colorError);
            }
            category.ColorVar = color.Trim();
        }
        if (req.SortOrder is { } order)
        {
            category.SortOrder = order;
        }
        // Archiving is a soft delete: historical entries keep resolving their category.
        if (req.Archived is { } archived)
        {
            category.ArchivedAt = archived ? clock.GetCurrentInstant() : null;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(category);
    }

    private static async Task<IResult> GetVendorsAsync(
        AiObservatoryDbContext db,
        CancellationToken ct,
        bool includeArchived = false
    )
    {
        var q = db.SpendVendors.AsNoTracking();
        if (!includeArchived)
        {
            q = q.Where(v => v.ArchivedAt == null);
        }

        return Results.Ok(await q.OrderBy(v => v.DisplayName).ToListAsync(ct));
    }

    private static async Task<IResult> GetVendorByIdAsync(Guid id, AiObservatoryDbContext db, CancellationToken ct)
    {
        var vendor = await db.SpendVendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
        return vendor is not null ? Results.Ok(vendor) : Results.NotFound();
    }

    private static async Task<IResult> CreateVendorAsync(
        SpendVendorRequest req,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        var key = Slug(req.Key);
        if (key is null || ValidateName(req.DisplayName) is not null)
        {
            return Results.BadRequest("Key must be a slug of 60 characters or fewer and DisplayName is required");
        }

        // Null is legitimate and common: CodeRabbit, Gitar and GitHub Actions have no
        // token estimate to compare against. Only a non-null unparseable value is an error.
        Provider? provider = null;
        if (!string.IsNullOrWhiteSpace(req.Provider))
        {
            if (!Enum.TryParse<Provider>(req.Provider, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return Results.BadRequest($"Unknown provider: {req.Provider}");
            }
            provider = parsed;
        }

        if (await db.SpendVendors.AnyAsync(v => v.Key == key, ct))
        {
            return Results.Conflict($"Vendor key already exists: {key}");
        }

        if (req.DefaultCategoryId is { } categoryId && !await db.SpendCategories.AnyAsync(c => c.Id == categoryId, ct))
        {
            return Results.BadRequest($"Unknown DefaultCategoryId: {categoryId}");
        }

        var vendor = new SpendVendor
        {
            Key = key,
            DisplayName = req.DisplayName.Trim(),
            Provider = provider,
            DefaultCategoryId = req.DefaultCategoryId,
        };
        db.SpendVendors.Add(vendor);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Same race as CreateCategoryAsync above: the pre-check passed for both
            // concurrent callers, the unique index only stops one of them.
            db.Entry(vendor).State = EntityState.Detached;
            return Results.Conflict($"Vendor key already exists: {key}");
        }
        return Results.CreatedAtRoute("GetSpendVendorById", new { id = vendor.Id }, vendor);
    }

    private static async Task<IResult> PatchVendorAsync(
        Guid id,
        SpendVendorPatchRequest req,
        AiObservatoryDbContext db,
        IClock clock,
        CancellationToken ct
    )
    {
        var vendor = await db.SpendVendors.FindAsync([id], ct);
        if (vendor is null)
        {
            return Results.NotFound();
        }

        if (req.DisplayName is { } name)
        {
            if (ValidateName(name) is { } error)
            {
                return Results.BadRequest(error);
            }
            vendor.DisplayName = name.Trim();
        }

        // Undefined means the caller did not mention the field at all -- leave the column
        // alone. Anything else (including an explicit JSON null) is a deliberate write.
        if (req.DefaultCategoryId.ValueKind is not JsonValueKind.Undefined)
        {
            var (categoryId, categoryError) = await ResolveDefaultCategoryAsync(req.DefaultCategoryId, db, ct);
            if (categoryError is not null)
            {
                return Results.BadRequest(categoryError);
            }
            vendor.DefaultCategoryId = categoryId;
        }

        if (req.Archived is { } archived)
        {
            vendor.ArchivedAt = archived ? clock.GetCurrentInstant() : null;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(vendor);
    }

    /// <summary>
    /// Reads a present <c>defaultCategoryId</c> patch value: JSON null clears the default,
    /// a GUID string repoints it (validated against the catalog), anything else is a 400.
    /// The absent case is handled by the caller — see SpendVendorPatchRequest for why the
    /// two cannot be distinguished by a plain <c>Guid?</c>.
    /// </summary>
    private static async Task<(Guid? CategoryId, string? Error)> ResolveDefaultCategoryAsync(
        JsonElement element,
        AiObservatoryDbContext db,
        CancellationToken ct
    )
    {
        if (element.ValueKind is JsonValueKind.Null)
        {
            return (null, null);
        }

        if (element.ValueKind is not JsonValueKind.String || !element.TryGetGuid(out var categoryId))
        {
            return (null, "DefaultCategoryId must be a GUID string or null");
        }

        return await db.SpendCategories.AnyAsync(c => c.Id == categoryId, ct)
            ? (categoryId, null)
            : (null, $"Unknown DefaultCategoryId: {categoryId}");
    }

    // The three validators below are internal rather than private so the unit lane can
    // exercise them directly: they gate what enters the catalog the ledger's totals group by,
    // and driving them through HTTP would move them into the integration project, outside
    // Stryker's lane.

    internal static string? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Length > 100
            ? "DisplayName is required and must be 100 characters or fewer"
            : null;

    internal static string? ValidateColorVar(string? color) =>
        color is { Length: > 60 } ? "ColorVar must be 60 characters or fewer" : null;

    /// <summary>Normalises a key to a lower-case slug, or null when it cannot be one.</summary>
    internal static string? Slug(string? raw)
    {
        var trimmed = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 60)
        {
            return null;
        }

        return trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? trimmed : null;
    }
}

public sealed record SpendCategoryRequest(string Key, string DisplayName, string? ColorVar, int SortOrder);

public sealed record SpendVendorRequest(string Key, string DisplayName, string? Provider, Guid? DefaultCategoryId);

public sealed record SpendCatalogPatchRequest(string? DisplayName, string? ColorVar, int? SortOrder, bool? Archived);

/// <param name="DefaultCategoryId">
/// Deliberately a <see cref="JsonElement"/> rather than a <c>Guid?</c>: the column is
/// itself nullable, so the patch needs three states, and System.Text.Json maps both an
/// omitted property and an explicit <c>null</c> onto a null <c>Guid?</c>. That collapse is
/// why a vendor's default category could be set at creation and then never cleared. An
/// omitted property leaves this at <c>default(JsonElement)</c> —
/// <see cref="JsonValueKind.Undefined"/> — while an explicit <c>null</c> arrives as
/// <see cref="JsonValueKind.Null"/>, so the two stay distinguishable.
/// </param>
public sealed record SpendVendorPatchRequest(string? DisplayName, JsonElement DefaultCategoryId, bool? Archived);
