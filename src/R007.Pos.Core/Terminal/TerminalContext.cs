namespace R007.Pos.Core.Terminal;

/// <summary>
/// Who and where this terminal is. There is one POS application for every station; its screens and
/// features are driven by the device registration, the facility's capabilities and the signed-in
/// staff member's permissions — all returned by the API, never hard-coded per station.
/// </summary>
/// <param name="DeviceId">Registered device identifier (assigned by the API at registration).</param>
/// <param name="FacilityId">Facility the device belongs to (e.g. Restaurant, Supermarket).</param>
/// <param name="OperatingPointId">Operating point/till within the facility.</param>
/// <param name="StaffId">Signed-in staff member (NFC + PIN at fixed stations), or <c>null</c> when locked.</param>
public sealed record TerminalContext(
    Guid DeviceId,
    Guid FacilityId,
    Guid OperatingPointId,
    Guid? StaffId = null)
{
    /// <summary>True when a staff member is signed in on this terminal.</summary>
    public bool HasStaff => StaffId is not null;
}
