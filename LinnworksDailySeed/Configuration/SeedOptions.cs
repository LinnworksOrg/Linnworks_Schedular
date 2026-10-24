namespace LinnworksDailySeed.Configuration;

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public string TimeZone { get; set; } = "India Standard Time";
    public string RunAt { get; set; } = "09:00";
    public int PurchaseOrderCount { get; set; } = 1;
    public int OrderCount { get; set; } = 1;
    public int RefundCount { get; set; } = 1;
    public string Source { get; set; } = "DIRECT";
    public string SubSource { get; set; } = "AUTO-SEED";
    public string LocationName { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string PaymentMethodName { get; set; } = "";
    public string PostalServiceName { get; set; } = "";
    public string RunLogDirectory { get; set; } = "runs";
}
