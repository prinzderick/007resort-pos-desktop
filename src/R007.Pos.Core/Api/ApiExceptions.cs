using System.Net;

namespace R007.Pos.Core.Api;

/// <summary>An RFC 7807 problem returned by the API. <see cref="Code"/> is the stable, machine-readable error code.</summary>
public sealed class ApiException(HttpStatusCode status, string code, string title, string? detail = null, IReadOnlyDictionary<string, string[]>? errors = null, Guid? approvalId = null)
    : Exception(string.IsNullOrEmpty(detail) ? title : $"{title}: {detail}")
{
    public HttpStatusCode Status { get; } = status;

    public string Code { get; } = code;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();

    /// <summary>Set on <c>approval_required</c> / <c>approval_pending</c> problems when the API already created the approval.</summary>
    public Guid? ApprovalId { get; } = approvalId;

    public bool IsUnauthorized => Status == HttpStatusCode.Unauthorized;

    public bool IsConflict => Status == HttpStatusCode.Conflict;

    public bool IsPermissionDenied => Status == HttpStatusCode.Forbidden && Code == "permission_denied";

    public bool IsApprovalRequired => Status == HttpStatusCode.Forbidden && Code == "approval_required";

    /// <summary>Message safe and useful to show to the operator.</summary>
    public string UserMessage => string.IsNullOrEmpty(Detail) ? Title : Detail;
}

/// <summary>The API could not be reached (network error, timeout, DNS, or 502/503/504 after retries).</summary>
public sealed class ApiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
