using LinnworksDailySeed.Models;

namespace LinnworksDailySeed.Catalog;

public sealed class ResolvedCatalog
{
    public required StockLocation Location { get; init; }
    public required IReadOnlyList<Supplier> Suppliers { get; init; }
    public required IReadOnlyList<PaymentMethod> PaymentMethods { get; init; }
    public required IReadOnlyList<PostageService> PostalServices { get; init; }
    public required IReadOnlyList<StockItem> Items { get; init; }
    public required IReadOnlyList<SeedCustomer> Customers { get; init; }
    public required IReadOnlyList<string> SubSources { get; init; }
    public required IReadOnlyList<CurrencyConversionRate> Currencies { get; init; }

    public Supplier PickSupplier(int slot)
    {
        var groups = Suppliers
            .GroupBy(s => string.IsNullOrWhiteSpace(s.Currency) ? "GBP" : s.Currency.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .ToList();
        if (groups.Count <= 1)
        {
            return Suppliers[slot % Suppliers.Count];
        }

        var group = groups[slot % groups.Count];
        return group[(slot / groups.Count) % group.Count];
    }

    public StockItem PickPurchaseItem(int slot) => Items[slot % Items.Count];

    public SeedCustomer PickCustomer(int slot) => Customers[slot % Customers.Count];

    public PaymentMethod PickPayment(int slot) => PaymentMethods[slot % PaymentMethods.Count];

    public PostageService PickPostal(int slot) => PostalServices[slot % PostalServices.Count];

    public CurrencyConversionRate PickCurrency(int slot) => Currencies[slot % Currencies.Count];

    public CurrencyConversionRate RateFor(string? currencyCode, int slot)
    {
        if (!string.IsNullOrWhiteSpace(currencyCode))
        {
            var match = Currencies.FirstOrDefault(c =>
                string.Equals(c.Currency, currencyCode, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match.ConversionRate > 0
                    ? match
                    : new CurrencyConversionRate { Currency = match.Currency, ConversionRate = 1 };
            }

            return new CurrencyConversionRate { Currency = currencyCode.Trim(), ConversionRate = 1 };
        }

        var picked = PickCurrency(slot);
        return picked.ConversionRate > 0
            ? picked
            : new CurrencyConversionRate { Currency = picked.Currency, ConversionRate = 1 };
    }

    /// <summary>
    /// A non-empty channel subsource. Linnworks rejects a blank subsource on CreateOrders;
    /// even-numbered orders are cleared after the order exists.
    /// </summary>
    public string PickSubSource(int slot)
    {
        if (SubSources.Count == 0)
        {
            return "DIRECT";
        }

        return SubSources[slot % SubSources.Count];
    }
}

public sealed class SeedCustomer
{
    public string FullName { get; init; } = "";
    public ChannelAddress Address { get; init; } = new();
}
