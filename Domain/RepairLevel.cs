namespace RecuperadorFirebird.Domain;

public enum RepairLevel
{
    Diagnostic = 1,
    LogicalRebuild,
    PageRepair,
    Mend,
    IgnoreChecksums
}

public enum RepairOutcome
{
    DiagnosticClean,
    DiagnosticIssues,
    RestoredAndValidated,
    RestoredWithWarnings,
    Failed,
    Cancelled
}
