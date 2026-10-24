namespace LinnworksDailySeed.RunLog;

public sealed class DailyRunRecord
{
    public string Date { get; set; } = "";
    /// <summary>
    /// Prefix for Linnworks references, e.g. AUTO-20261023 or AUTO-20261023-F143055 for a forced re-run.
    /// </summary>
    public string ReferencePrefix { get; set; } = "";
    public bool Forced { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Status { get; set; } = "InProgress";
    public string? LocationName { get; set; }
    public string? SupplierName { get; set; }
    public string? Sku { get; set; }
    public List<PurchaseOrderRunItem> PurchaseOrders { get; set; } = new();
    public List<OrderRunItem> Orders { get; set; } = new();
    public List<RefundRunItem> Refunds { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class PurchaseOrderRunItem
{
    public string Reference { get; set; } = "";
    public Guid? PurchaseId { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
}

public sealed class OrderRunItem
{
    public string Reference { get; set; } = "";
    public Guid? OrderId { get; set; }
    public bool Processed { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
}

public sealed class RefundRunItem
{
    public string OrderReference { get; set; } = "";
    public Guid? OrderId { get; set; }
    public int? RefundHeaderId { get; set; }
    public bool Actioned { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Error { get; set; }
}
