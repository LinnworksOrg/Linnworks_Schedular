using LinnworksDailySeed.Configuration;
using LinnworksDailySeed.Linnworks;
using LinnworksDailySeed.Models;
using Microsoft.Extensions.Options;

namespace LinnworksDailySeed.Catalog;

public sealed class AccountCatalog
{
    private readonly LinnworksClient _client;
    private readonly SeedOptions _options;
    private readonly ILogger<AccountCatalog> _logger;

    public AccountCatalog(
        LinnworksClient client,
        IOptions<SeedOptions> options,
        ILogger<AccountCatalog> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ResolvedCatalog> ResolveAsync(CancellationToken cancellationToken)
    {
        var locations = await _client.GetStockLocationsAsync(cancellationToken).ConfigureAwait(false);
        var location = ResolveLocation(locations);

        var suppliers = await _client.GetSuppliersAsync(cancellationToken).ConfigureAwait(false);
        var supplierPool = ResolveSuppliers(suppliers);

        var paymentMethods = await _client.GetPaymentMethodsAsync(cancellationToken).ConfigureAwait(false);
        var paymentPool = ResolvePaymentMethods(paymentMethods);

        var shippingMethods = await _client.GetShippingMethodsAsync(cancellationToken).ConfigureAwait(false);
        var postalPool = ResolvePostalServices(shippingMethods);

        var items = await LoadStockItemsAsync(location.StockLocationId, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                $"No usable stock items were found at location '{location.LocationName}'. The seed needs existing SKUs for purchase orders and orders.");
        }

        var customers = await LoadCustomersAsync(cancellationToken).ConfigureAwait(false);
        if (customers.Count == 0)
        {
            throw new InvalidOperationException(
                "No existing customers were found on processed orders or in customer lookup. The seed will not create a placeholder customer.");
        }

        var subSources = await LoadSubSourcesAsync(cancellationToken).ConfigureAwait(false);
        var currencies = await LoadCurrenciesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Catalog resolved. Location={Location}, Suppliers={SupplierCount}, Payments={PaymentCount}, Postal={PostalCount}, Items={ItemCount}, AvailableNow={AvailableCount}, Customers={CustomerCount}, SubSources={SubSourceCount}, Currencies={Currencies}",
            location.LocationName,
            supplierPool.Count,
            paymentPool.Count,
            postalPool.Count,
            items.Count,
            items.Count(i => i.Available >= 1),
            customers.Count,
            subSources.Count,
            string.Join(", ", currencies.Select(c => c.Currency)));

        return new ResolvedCatalog
        {
            Location = location,
            Suppliers = supplierPool,
            PaymentMethods = paymentPool,
            PostalServices = postalPool,
            Items = items,
            Customers = customers,
            SubSources = subSources,
            Currencies = currencies
        };
    }

    private StockLocation ResolveLocation(IReadOnlyList<StockLocation> locations)
    {
        var nonFulfilment = locations
            .Where(l => !l.IsFulfillmentCenter)
            .ToList();

        if (!string.IsNullOrWhiteSpace(_options.LocationName))
        {
            var match = nonFulfilment.FirstOrDefault(l =>
                string.Equals(l.LocationName, _options.LocationName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"Configured LocationName '{_options.LocationName}' was not found among non-fulfilment locations: {Names(nonFulfilment.Select(l => l.LocationName))}.");
            }

            return match;
        }

        return nonFulfilment.FirstOrDefault(l =>
                   string.Equals(l.LocationName, "Default", StringComparison.OrdinalIgnoreCase))
               ?? nonFulfilment.FirstOrDefault()
               ?? throw new InvalidOperationException("No non-fulfilment stock location was found.");
    }

    private List<Supplier> ResolveSuppliers(IReadOnlyList<Supplier> suppliers)
    {
        var named = suppliers
            .Where(s => s.pkSupplierID != Guid.Empty && !string.IsNullOrWhiteSpace(s.SupplierName))
            .OrderBy(s => s.SupplierName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(_options.SupplierName))
        {
            var match = named.FirstOrDefault(s =>
                string.Equals(s.SupplierName, _options.SupplierName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"Configured SupplierName '{_options.SupplierName}' was not found. Available: {Names(named.Select(s => s.SupplierName))}.");
            }

            return [match];
        }

        if (named.Count == 0)
        {
            throw new InvalidOperationException("No suppliers were found. Create at least one supplier before running the seed.");
        }

        return named;
    }

    private List<PaymentMethod> ResolvePaymentMethods(IReadOnlyList<PaymentMethod> methods)
    {
        var named = methods
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(_options.PaymentMethodName))
        {
            var match = named.FirstOrDefault(m =>
                string.Equals(m.Name, _options.PaymentMethodName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"Configured PaymentMethodName '{_options.PaymentMethodName}' was not found. Available: {Names(named.Select(m => m.Name))}.");
            }

            return [match];
        }

        if (named.Count == 0)
        {
            throw new InvalidOperationException("No payment methods were found.");
        }

        return named;
    }

    private List<PostageService> ResolvePostalServices(IReadOnlyList<ShippingMethod> shippingMethods)
    {
        var services = shippingMethods
            .SelectMany(m => (m.PostalServices ?? new List<PostageService>()).Select(s =>
            {
                if (string.IsNullOrWhiteSpace(s.Vendor))
                {
                    s.Vendor = m.Vendor;
                }

                return s;
            }))
            .Where(s => s.pkPostalServiceId != Guid.Empty && !string.IsNullOrWhiteSpace(s.PostalServiceName))
            .GroupBy(s => s.PostalServiceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.PostalServiceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(_options.PostalServiceName))
        {
            var match = services.FirstOrDefault(s =>
                string.Equals(s.PostalServiceName, _options.PostalServiceName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new InvalidOperationException(
                    $"Configured PostalServiceName '{_options.PostalServiceName}' was not found. Available: {Names(services.Select(s => s.PostalServiceName))}.");
            }

            return [match];
        }

        var internalServices = services.Where(s => IsInternalVendor(s.Vendor)).ToList();
        var broad = services
            .Where(s => IsInternalVendor(s.Vendor) || !s.PostalServiceName!.Contains(" for ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var pool = internalServices.Count >= 2
            ? internalServices
            : broad.Count >= 2 ? broad : services;
        if (pool.Count == 0)
        {
            throw new InvalidOperationException("No postal services were found.");
        }

        return pool;
    }

    private async Task<List<StockItem>> LoadStockItemsAsync(Guid locationId, CancellationToken cancellationToken)
    {
        var items = new List<StockItem>();
        var seen = new HashSet<Guid>();
        const int pageSize = 100;
        const int maxPages = 8;

        for (var page = 1; page <= maxPages; page++)
        {
            var result = await _client.GetStockItemsAsync(locationId, page, pageSize, cancellationToken)
                .ConfigureAwait(false);
            if (result?.Data is null || result.Data.Count == 0)
            {
                break;
            }

            foreach (var item in result.Data)
            {
                if (item.StockItemId == Guid.Empty || !IsRealSku(item.ItemNumber))
                {
                    continue;
                }

                if (seen.Add(item.StockItemId))
                {
                    items.Add(item);
                }
            }

            if (page >= result.TotalPages || result.Data.Count < pageSize)
            {
                break;
            }
        }

        return items
            .OrderBy(i => i.ItemNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<SeedCustomer>> LoadCustomersAsync(CancellationToken cancellationToken)
    {
        var customers = new List<SeedCustomer>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var page = 1; page <= 5 && customers.Count < 40; page++)
        {
            var processed = await _client.SearchProcessedOrdersAsync(
                DateTime.UtcNow.AddYears(-2),
                DateTime.UtcNow.AddDays(1),
                page,
                resultsPerPage: 100,
                cancellationToken).ConfigureAwait(false);
            var rows = processed.ProcessedOrders?.Data ?? new List<ProcessedOrderWeb>();
            if (rows.Count == 0)
            {
                break;
            }

            foreach (var order in rows)
            {
                TryAddCustomer(customers, seen, FromProcessedOrder(order));
            }

            if (rows.Count < 100)
            {
                break;
            }
        }

        var beforeLookup = customers.Count;
        foreach (var prefix in new[] { "a", "e", "i", "j", "m", "s", "b", "c", "d", "r" })
        {
            if (customers.Count >= 40)
            {
                break;
            }

            try
            {
                var found = await _client.CustomerLookUpAsync("Name", prefix, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var address in found ?? new List<CustomerAddress>())
                {
                    TryAddCustomer(customers, seen, FromLookup(address));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CustomerLookUp failed for name prefix {Prefix}", prefix);
            }
        }

        _logger.LogInformation(
            "Customers loaded. FromOrders={FromOrders}, Total={Total}",
            beforeLookup,
            customers.Count);

        return customers
            .OrderBy(c => c.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<List<string>> LoadSubSourcesAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.SubSource))
        {
            return [_options.SubSource.Trim()];
        }

        try
        {
            var channels = await _client.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
            return (channels ?? new List<ChannelHeader>())
                .Select(c => c.SubSource?.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetChannels failed. Orders will use an empty subsource.");
            return [];
        }
    }

    private async Task<List<CurrencyConversionRate>> LoadCurrenciesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rates = await _client.GetCurrencyConversionRatesAsync(cancellationToken).ConfigureAwait(false);
            var distinct = (rates ?? new List<CurrencyConversionRate>())
                .Where(r => !string.IsNullOrWhiteSpace(r.Currency))
                .GroupBy(r => r.Currency, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(r => r.Currency, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinct.Count > 0)
            {
                return distinct;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetCurrencyConversionRates failed. Falling back to GBP.");
        }

        return [new CurrencyConversionRate { Currency = "GBP", ConversionRate = 1 }];
    }

    private static void TryAddCustomer(List<SeedCustomer> customers, HashSet<string> seen, SeedCustomer? customer)
    {
        if (customer is null || string.IsNullOrWhiteSpace(customer.FullName))
        {
            return;
        }

        if (IsPlaceholderCustomer(customer.FullName))
        {
            return;
        }

        var key = customer.FullName.Trim();
        if (seen.Add(key))
        {
            customers.Add(customer);
        }
    }

    private static SeedCustomer? FromProcessedOrder(ProcessedOrderWeb order)
    {
        var name = First(order.cFullName, order.BillingName, order.ChannelBuyerName);
        var address1 = First(order.Address1, order.BillingAddress1, FirstLine(order.cShippingAddress));
        var town = First(order.Town, order.BillingTown);
        var country = First(order.cCountry, order.BillingCountryName);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(address1) && string.IsNullOrWhiteSpace(town))
        {
            return null;
        }

        return new SeedCustomer
        {
            FullName = name,
            Address = new ChannelAddress
            {
                FullName = name,
                Company = First(order.Company, order.BillingCompany),
                Address1 = string.IsNullOrWhiteSpace(address1) ? town : address1,
                Address2 = First(order.Address2, order.BillingAddress2),
                Address3 = First(order.Address3, order.BillingAddress3),
                Town = town,
                Region = First(order.Region, order.BillingRegion),
                PostCode = First(order.cPostCode, order.BillingPostCode),
                Country = country,
                CountryCode = country.Length == 2 ? country.ToUpperInvariant() : "",
                PhoneNumber = First(order.BuyerPhoneNumber, order.BillingPhoneNumber),
                EmailAddress = First(order.cEmailAddress, order.BillingEmailAddress)
            }
        };
    }

    private static SeedCustomer? FromLookup(CustomerAddress address)
    {
        var name = address.FullName?.Trim();
        var country = address.Country?.Trim();
        var address1 = address.Address1?.Trim();
        var town = address.Town?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(country))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(address1) && string.IsNullOrWhiteSpace(town))
        {
            return null;
        }

        return new SeedCustomer
        {
            FullName = name,
            Address = new ChannelAddress
            {
                FullName = name,
                Company = address.Company?.Trim() ?? "",
                Address1 = string.IsNullOrWhiteSpace(address1) ? town! : address1,
                Address2 = address.Address2?.Trim() ?? "",
                Address3 = address.Address3?.Trim() ?? "",
                Town = town ?? "",
                Region = address.Region?.Trim() ?? "",
                PostCode = address.PostCode?.Trim() ?? "",
                Country = country,
                CountryCode = country.Length == 2 ? country.ToUpperInvariant() : "",
                PhoneNumber = address.PhoneNumber?.Trim() ?? "",
                EmailAddress = address.EmailAddress?.Trim() ?? ""
            }
        };
    }

    private static bool IsRealSku(string? sku)
        => !string.IsNullOrWhiteSpace(sku) && sku.Any(char.IsLetterOrDigit);

    private static bool IsPlaceholderCustomer(string name)
        => name.Contains("AUTO-SEED", StringComparison.OrdinalIgnoreCase)
           || name.Contains("AUTO SEED", StringComparison.OrdinalIgnoreCase);

    private static bool IsInternalVendor(string? vendor)
    {
        if (string.IsNullOrWhiteSpace(vendor))
        {
            return true;
        }

        return vendor.Equals("NONE", StringComparison.OrdinalIgnoreCase)
               || vendor.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)
               || vendor.Equals("OTHER", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var line = value.Split('\n', '\r', ',')[0].Trim();
        return line.Length <= 90 ? line : line[..90];
    }

    private static string First(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string Names(IEnumerable<string?> values)
        => string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => $"'{v}'"));
}
