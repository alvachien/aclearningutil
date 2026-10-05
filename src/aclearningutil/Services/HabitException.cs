using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace aclearningutil.Services;

/// <summary>
/// Business-rule failure for the habit-tracking API. Carries a stable machine-readable
/// code (camelCase — see docs/design-habit-api.md § Errors) that the UI maps to an
/// i18n message; <see cref="Detail"/> is a human-readable message naming the offender
/// where applicable (e.g. the unknown name for unknownOperandName).
/// </summary>
public sealed class HabitException : Exception
{
    public string Code { get; }
    public int StatusCode { get; }
    public string Detail { get; }

    public HabitException(int statusCode, string code, string detail) : base(detail)
    {
        StatusCode = statusCode;
        Code = code;
        Detail = detail;
    }

    /// <summary>404 — also used for cross-tenant access (never reveals existence).</summary>
    public static HabitException NotFound(string resource = "Resource") =>
        new(StatusCodes.Status404NotFound, HabitErrorCodes.NotFound, $"{resource} not found.");

    /// <summary>
    /// 401 — the authenticated token carries no usable user-id claim. Every habit
    /// failure response goes through this exception so the filter's RFC 7807 problem
    /// document (title + extensions.code) is returned instead of a bare body.
    /// </summary>
    public static HabitException Unauthenticated(string detail) =>
        new(StatusCodes.Status401Unauthorized, HabitErrorCodes.Unauthenticated, detail);

    /// <summary>422 — validation / state rule violation.</summary>
    public static HabitException Unprocessable(string code, string detail) =>
        new(StatusCodes.Status422UnprocessableEntity, code, detail);
}

/// <summary>Stable machine-readable error codes returned in ProblemDetails.extensions.code.</summary>
public static class HabitErrorCodes
{
    public const string NotFound = "notFound";
    public const string HabitInactive = "habitInactive";
    public const string AlreadyInactive = "alreadyInactive";
    public const string ReactivationNotSupported = "reactivationNotSupported";
    public const string OutOfWindow = "outOfWindow";
    public const string InvalidDateRange = "invalidDateRange";
    public const string DuplicateEntry = "duplicateEntry";
    public const string DuplicateName = "duplicateName";
    public const string StructuralChangeBlocked = "structuralChangeBlocked";
    public const string ItemHasPunches = "itemHasPunches";
    public const string MissingCycleTarget = "missingCycleTarget";
    public const string InvalidTarget = "invalidTarget";
    public const string InvalidThreshold = "invalidThreshold";
    public const string NoItems = "noItems";
    public const string ItemWithoutProperties = "itemWithoutProperties";
    public const string NoCriteria = "noCriteria";
    public const string NoRootCriterion = "noRootCriterion";
    public const string RootRequired = "rootRequired";
    public const string RootCriterionProtected = "rootCriterionProtected";
    public const string CircularCriterion = "circularCriterion";
    public const string CriterionInUse = "criterionInUse";
    public const string InvalidOperandCount = "invalidOperandCount";
    public const string UnknownOperandName = "unknownOperandName";
    public const string InvalidPropertyType = "invalidPropertyType";
    public const string InvalidBaseRate = "invalidBaseRate";
    public const string InvalidItemUniqueness = "invalidItemUniqueness";
    public const string InvalidName = "invalidName";
    public const string MissingCycle = "missingCycle";
    public const string MissingPropertyType = "missingPropertyType";
    public const string MissingCriterionType = "missingCriterionType";
    public const string MissingShareOwnerName = "missingShareOwnerName";
    public const string InvalidGrantee = "invalidGrantee";
    public const string ScopeItemsRequired = "scopeItemsRequired";
    public const string InvalidEnumValue = "invalidEnumValue";
    public const string DuplicateReference = "duplicateReference";
    public const string ValidationTooLong = "validationTooLong";
    public const string TooManyEntries = "tooManyEntries";

    /// <summary>401 — the caller's authenticated token carries no user-id claim.</summary>
    public const string Unauthenticated = "unauthenticated";
}

/// <summary>
/// Converts <see cref="HabitException"/> into an RFC 7807 ProblemDetails response with
/// the code in extensions. Also maps infrastructure-level failures on the habit write
/// paths: <see cref="DbUpdateException"/> caused by a unique-index violation maps to
/// <c>duplicateName</c> (the design's promise that name conflicts surface with that code
/// even when the app-level pre-check loses a race); any other database failure yields an
/// RFC 7807 500 (never an empty body). A <see cref="JsonException"/> raised while binding
/// the request body — the enum converter rejects integer/out-of-range values with
/// <c>allowIntegerValues: false</c> — becomes 422 <c>invalidEnumValue</c>; note that in
/// the normal MVC pipeline a body-binding failure never reaches this filter (the input
/// formatter turns it into a 400 validation ProblemDetails first) — this branch is the
/// safety net for JsonExceptions raised after binding.
/// Applied to all habit-tracking controllers.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HabitExceptionFilterAttribute : ExceptionFilterAttribute
{
    /// <summary>SQLite PRIMARY KEY / UNIQUE / FK errors (SQLITE_CONSTRAINT).</summary>
    internal const int SqliteConstraintErrorCode = 19;

    /// <summary>
    /// The failed-constraint column signatures of the name-uniqueness indexes plus the
    /// one-grant-per-(habit, user) share index (the SQLite message reports qualified
    /// columns, not index names). A duplicate share invite loses the same race story as
    /// a duplicate name: both mean "already exists in this scope" → duplicateName. The
    /// partial single-root index ("Criteria.HabitId") is intentionally absent: a lost
    /// root-switch race is not a duplicate and falls through to the generic 500 problem
    /// document.
    /// </summary>
    internal static readonly string[] DuplicateNameConstraintSignatures =
    [
        "HabitItems.HabitId, HabitItems.Name",
        "ItemProperties.ItemId, ItemProperties.Name",
        "Criteria.HabitId, Criteria.Name",
        "HabitShareGrants.HabitId, HabitShareGrants.GranteeUserId",
    ];

    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is HabitException ex)
        {
            Emit(context, ex.StatusCode, ReasonPhrase(ex.StatusCode), ex.Detail, ex.Code);
            return;
        }

        if (context.Exception is DbUpdateException dbEx)
        {
            HandleDatabaseUpdate(context, dbEx);
            return;
        }

        if (context.Exception is JsonException jsonEx)
        {
            HandleJsonBinding(context, jsonEx);
        }
    }

    private static void HandleDatabaseUpdate(ExceptionContext context, DbUpdateException dbEx)
    {
        if (dbEx.InnerException is SqliteException { SqliteErrorCode: SqliteConstraintErrorCode } sqlite
            && sqlite.Message.Contains("UNIQUE constraint failed", StringComparison.Ordinal)
            && DuplicateNameConstraintSignatures.Any(sig => sqlite.Message.Contains(sig, StringComparison.Ordinal)))
        {
            Emit(context, StatusCodes.Status422UnprocessableEntity,
                ReasonPhrase(StatusCodes.Status422UnprocessableEntity),
                "A record with the same name already exists in this scope.",
                HabitErrorCodes.DuplicateName);
            return;
        }

        LogError(context, dbEx, "Database update failed during a habit-tracking request.");
        Emit(context, StatusCodes.Status500InternalServerError,
            ReasonPhrase(StatusCodes.Status500InternalServerError),
            "The request could not be persisted due to a database error.",
            code: null);
    }

    private static void HandleJsonBinding(ExceptionContext context, JsonException jsonEx)
    {
        // Enum members are strings only on the wire (allowIntegerValues: false); a rejected
        // integer / unknown string arrives here. Malformed JSON without a property path
        // keeps the pipeline's default handling.
        if (jsonEx.Path is null)
        {
            return;
        }

        LogError(context, jsonEx, "Request body failed JSON binding at {Path}.", jsonEx.Path);
        Emit(context, StatusCodes.Status422UnprocessableEntity,
            ReasonPhrase(StatusCodes.Status422UnprocessableEntity),
            $"The value at '{jsonEx.Path}' is not valid JSON for this request (enum members must be their string names).",
            HabitErrorCodes.InvalidEnumValue);
    }

    private static void Emit(ExceptionContext context, int statusCode, string title, string detail, string? code)
    {
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
        };
        if (code is not null)
        {
            problem.Extensions["code"] = code;
        }

        context.Result = new ObjectResult(problem) { StatusCode = statusCode };
        context.ExceptionHandled = true;
    }

    private static void LogError(ExceptionContext context, Exception exception, string message, params object?[] args)
    {
        var logger = context.HttpContext.RequestServices
            .GetService<ILogger<HabitExceptionFilterAttribute>>();
        logger?.LogError(exception, message, args);
    }

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status422UnprocessableEntity => "Unprocessable Content",
        StatusCodes.Status500InternalServerError => "Internal Server Error",
        _ => "Error",
    };
}
