namespace SRC.Authorization;

/// <summary>
/// The seven roles configured across the Wonrich Dairy services (SCRUM-34 + Processing). A user holds exactly
/// one of these at a time, assigned through user management (SCRUM-45).
/// This file is an EXACT COPY from Auth shared service (shared/Auth/WonrichRoles.cs) - must stay in sync.
/// </summary>
public static class WonrichRoles
{
    public const string SystemAdministrator = "SystemAdministrator";
    public const string MccManager = "MccManager";
    public const string IntakeOfficer = "IntakeOfficer";
    public const string QualityAnalyst = "QualityAnalyst";
    public const string FactoryIntakeOfficer = "FactoryIntakeOfficer";
    public const string ProductionManager = "ProductionManager";
    public const string ProcessingTechnician = "ProcessingTechnician";

    public static readonly IReadOnlyList<string> All =
    [
        SystemAdministrator,
        MccManager,
        IntakeOfficer,
        QualityAnalyst,
        FactoryIntakeOfficer,
        ProductionManager,
        ProcessingTechnician
    ];

    public static bool IsConfigured(string? role) =>
        role is not null && All.Contains(role, StringComparer.Ordinal);
}
