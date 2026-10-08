namespace SRC.Authorization;

/// <summary>
/// The eight roles configured across the Wonrich Dairy services (SCRUM-34). A user holds exactly
/// one of these at a time, assigned through user management (SCRUM-45).
/// </summary>
/// <remarks>
/// These names travel inside the JWT as role claims and are the vocabulary every service's
/// authorization policies are written against, so they must not be renamed casually: a rename
/// invalidates tokens already issued and silently widens or narrows access in every service.
/// </remarks>
public static class WonrichRoles
{
    /// <summary>Full access, including user administration.</summary>
    public const string SystemAdministrator = "SystemAdministrator";

    /// <summary>Runs a chilling centre: maintains societies, oversees intake, and signs milk out to the factory.</summary>
    public const string MccManager = "MccManager";

    /// <summary>Registers arriving consignments at the chilling centre gate.</summary>
    public const string IntakeOfficer = "IntakeOfficer";

    /// <summary>Runs quality test panels and accepts or rejects consignments.</summary>
    public const string QualityAnalyst = "QualityAnalyst";

    /// <summary>Screens arrivals and creates batches at factory intake.</summary>
    public const string FactoryIntakeOfficer = "FactoryIntakeOfficer";

    /// <summary>Oversees processing and traces batches back to their sources.</summary>
    public const string ProductionManager = "ProductionManager";

    /// <summary>Works the factory floor: records tanker unloads, allocates tanks, and logs processing runs and tank temperatures.</summary>
    public const string ProcessingTechnician = "ProcessingTechnician";

    /// <summary>Carries out quality control checks on product during and after processing.</summary>
    public const string QualityControlOfficer = "QualityControlOfficer";

    /// <summary>Every configured role, for validation and for the user management role picker.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        SystemAdministrator,
        MccManager,
        IntakeOfficer,
        QualityAnalyst,
        FactoryIntakeOfficer,
        ProductionManager,
        ProcessingTechnician,
        QualityControlOfficer
    ];

    /// <summary>Whether the supplied name is one of the configured roles.</summary>
    public static bool IsConfigured(string? role) =>
        role is not null && All.Contains(role, StringComparer.Ordinal);
}