namespace LinnworksDailySeed.Scenarios;

/// <summary>
/// Everyday mix from Linnworks_Seed_Scenarios_Catalog.xlsx (Recommended Daily Matrix),
/// plus the other RecommendedForDailySeed rows that are real Linnworks writes:
/// PO-FX, PO-ADDITIONAL-COST, and the cancel/return rows already on the matrix.
/// Attribute scenarios (tax inclusive, paid, invoice printed, manual refund, and so on)
/// are folded into these orders and recorded on <c>AlsoCovers</c>.
/// SyncTools rows are not created here; they run when SyncTools downloads this data.
/// </summary>
public static class DailyScenarioPlan
{
    public static IReadOnlyList<PurchaseScenarioSpec> PurchaseOrders { get; } =
    [
        new("PO-FULL-DELIVER", 1, LineCount: 1, Quantity: 40, Partial: false, DeliverQuantity: 40, Postage: false, ForeignCurrency: false, AdditionalCost: false),
        new("PO-PARTIAL-DELIVER", 3, LineCount: 1, Quantity: 10, Partial: true, DeliverQuantity: 4, Postage: false, ForeignCurrency: false, AdditionalCost: false),
        new("PO-MULTI-LINE", 2, LineCount: 2, Quantity: 30, Partial: false, DeliverQuantity: 30, Postage: false, ForeignCurrency: false, AdditionalCost: false),
        new("PO-SHIPPING-COST", 2, LineCount: 1, Quantity: 20, Partial: false, DeliverQuantity: 20, Postage: true, ForeignCurrency: false, AdditionalCost: false),
        new("PO-FX", 1, LineCount: 1, Quantity: 20, Partial: false, DeliverQuantity: 20, Postage: false, ForeignCurrency: true, AdditionalCost: false),
        new("PO-ADDITIONAL-COST", 1, LineCount: 1, Quantity: 15, Partial: false, DeliverQuantity: 15, Postage: false, ForeignCurrency: false, AdditionalCost: true)
    ];

    public static IReadOnlyList<OrderScenarioSpec> Orders { get; } =
    [
        new("ORD-BASE-PAID", 1, Process: true),
        new("ORD-TAX-EXCL", 5, Process: true),
        new("ORD-UNPAID", 3, Process: false),
        new("ORD-NO-PROCESS", 2, Process: false),
        new("ORD-SHIP-COST", 5, Process: true),
        new("ORD-DISC-LINE", 4, Process: true),
        new("ORD-DISC-ORDER", 3, Process: true),
        new("ORD-SERVICE-LINE", 3, Process: true),
        new("ORD-QTY-GT1", 4, Process: true),
        new("ORD-MULTI-LINE", 5, Process: true),
        new("ORD-COMPOSITE", 3, Process: true),
        new("ORD-CURRENCY-NONBASE", 3, Process: true),
        new("ORD-STATE-HOLD", 2, Process: false),
        new("ORD-ADV-TAX-RULE", 3, Process: true)
    ];

    public static IReadOnlyList<OrderScenarioSpec> Cancels { get; } =
    [
        new("CNL-OPEN-ORDER", 2, Process: false),
        new("CNL-WITH-REFUND", 2, Process: false)
    ];

    public static IReadOnlyList<RefundScenarioSpec> Refunds { get; } =
    [
        new("RFD-ITEM-FULL", "ORD-BASE-PAID", 1),
        new("RFD-SHIPPING", "ORD-SHIP-COST", 3),
        new("RFD-SERVICE", "ORD-SERVICE-LINE", 2),
        new("RFD-ITEM-PARTIAL-AMT", "ORD-TAX-EXCL", 3),
        new("RFD-ITEM-PARTIAL-QTY", "ORD-QTY-GT1", 2),
        new("RFD-MULTI-UNIT", "ORD-DISC-ORDER", 3),
        new("RFD-ADDITIONAL", "ORD-MULTI-LINE", 2)
    ];

    public static IReadOnlyList<ReturnScenarioSpec> Returns { get; } =
    [
        new("RET-RETURN", "ORD-CURRENCY-NONBASE", 2, WithRefund: false),
        new("RET-RETURN-REFUND", "ORD-ADV-TAX-RULE", 2, WithRefund: true)
    ];

    public static int OrderTotal => Orders.Sum(s => s.Count) + Cancels.Sum(s => s.Count);

    public static int PurchaseOrderTotal => PurchaseOrders.Sum(s => s.Count);

    public static int RefundTotal => Refunds.Sum(s => s.Count);
}

public sealed record PurchaseScenarioSpec(
    string ScenarioId,
    int Count,
    int LineCount,
    int Quantity,
    bool Partial,
    int DeliverQuantity,
    bool Postage,
    bool ForeignCurrency,
    bool AdditionalCost);

public sealed record OrderScenarioSpec(string ScenarioId, int Count, bool Process);

public sealed record RefundScenarioSpec(string ScenarioId, string SourceScenarioId, int Count);

public sealed record ReturnScenarioSpec(string ScenarioId, string SourceScenarioId, int Count, bool WithRefund);
