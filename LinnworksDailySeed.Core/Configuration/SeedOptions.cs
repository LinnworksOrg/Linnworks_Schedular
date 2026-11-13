namespace LinnworksDailySeed.Configuration;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string TimeZone { get; set; } = "India Standard Time";
    public string RunAt { get; set; } = "09:00";
    /// <summary>
    /// Documented size of the daily scenario matrix. The matrix in DailyScenarioPlan decides what is created.
    /// </summary>
    public int PurchaseOrderCount { get; set; } = 10;
    public int OrderCount { get; set; } = 50;
    public int RefundCount { get; set; } = 16;
    public string Source { get; set; } = "DIRECT";
    public string SubSource { get; set; } = "AUTO-SEED";
    public string LocationName { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string PaymentMethodName { get; set; } = "";
    public string PostalServiceName { get; set; } = "";
    public string RunLogDirectory { get; set; } = "runs";
}
