using System.Diagnostics.CodeAnalysis;

namespace SignalGrid.Ampelum.Host.ErrorHandling;

/// <summary>
/// Internal mirror of <see cref="Contracts.ErrorCodes"/>.
/// Stable error codes the host maps exceptions to in <see cref="ProblemDetailsMiddleware"/>.
/// </summary>
[Experimental("AMPELUM_V09_PREVIEW")]
internal static class ErrorCodes
{
    /// <summary>400 malformed JSON.</summary>
    public const string BadRequest                = "bad_request";
    /// <summary>404 generic.</summary>
    public const string NotFound                  = "not_found";
    /// <summary>404 unknown module.</summary>
    public const string ModuleNotFound            = "module_not_found";
    /// <summary>404 unknown prompt version.</summary>
    public const string VersionNotFound           = "version_not_found";
    /// <summary>404 unknown trace id.</summary>
    public const string TraceNotFound             = "trace_not_found";
    /// <summary>404 unknown optimizer run.</summary>
    public const string RunNotFound               = "run_not_found";
    /// <summary>422 signature-contract failed.</summary>
    public const string SignatureValidationFailed = "signature_validation_failed";
    /// <summary>409 version not 'challenger'.</summary>
    public const string PromoteConflict           = "promote_conflict";
    /// <summary>429 budget circuit open.</summary>
    public const string BudgetExceeded            = "budget_exceeded";
    /// <summary>503 deterministic broken.</summary>
    public const string ReplayLmCalled            = "replay_lm_called";
    /// <summary>503 promote split-brain.</summary>
    public const string SplitBrain                = "split_brain";
    /// <summary>500 unhandled.</summary>
    public const string InternalError             = "internal_error";

    /// <summary>All error codes in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        BadRequest, NotFound, ModuleNotFound, VersionNotFound, TraceNotFound, RunNotFound,
        SignatureValidationFailed, PromoteConflict, BudgetExceeded, ReplayLmCalled,
        SplitBrain, InternalError,
    };
}
