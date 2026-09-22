namespace Otueke.Pos.Core.Api;

/// <summary>
/// Client-side mirror of the API's <c>SystemInfoResponse</c> (<c>GET /api/v1/system/info</c>).
/// Will be replaced by the shared <c>Otueke.Contracts</c> package once it is published.
/// </summary>
public sealed record SystemInfo(string Service, string Version, string Environment, string Mode);
