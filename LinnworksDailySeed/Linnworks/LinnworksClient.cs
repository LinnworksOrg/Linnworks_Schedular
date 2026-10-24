using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LinnworksDailySeed.Models;

namespace LinnworksDailySeed.Linnworks;

public sealed class LinnworksClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AuthClient _authClient;
    private readonly ILogger<LinnworksClient> _logger;

    public LinnworksClient(
        IHttpClientFactory httpClientFactory,
        AuthClient authClient,
        ILogger<LinnworksClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _authClient = authClient;
        _logger = logger;
    }

    public Task<List<StockLocation>> GetStockLocationsAsync(CancellationToken cancellationToken)
        => PostFormAsync<List<StockLocation>>("Inventory/GetStockLocations", "", cancellationToken);

    public Task<List<Supplier>> GetSuppliersAsync(CancellationToken cancellationToken)
        => PostFormAsync<List<Supplier>>("Inventory/GetSuppliers", "", cancellationToken);

    public Task<List<PaymentMethod>> GetPaymentMethodsAsync(CancellationToken cancellationToken)
        => PostFormAsync<List<PaymentMethod>>("Orders/GetPaymentMethods", "", cancellationToken);

    public Task<List<ShippingMethod>> GetShippingMethodsAsync(CancellationToken cancellationToken)
        => PostFormAsync<List<ShippingMethod>>("Orders/GetShippingMethods", "", cancellationToken);

    public Task<List<CurrencyConversionRate>> GetCurrencyConversionRatesAsync(CancellationToken cancellationToken)
        => GetAsync<List<CurrencyConversionRate>>(
            "Settings/GetCurrencyConversionRates?getCurrenciesFromOrders=true",
            cancellationToken);

    public Task<List<ChannelHeader>> GetChannelsAsync(CancellationToken cancellationToken)
        => GetAsync<List<ChannelHeader>>("Inventory/GetChannels", cancellationToken);

    public Task<List<CustomerAddress>> CustomerLookUpAsync(
        string field,
        string text,
        CancellationToken cancellationToken)
    {
        var extension =
            $"Orders/CustomerLookUp?field={Uri.EscapeDataString(field)}&txt={Uri.EscapeDataString(text)}";
        return GetAsync<List<CustomerAddress>>(extension, cancellationToken);
    }

    public Task<SearchProcessedOrdersResponse> SearchProcessedOrdersAsync(
        DateTime fromDate,
        DateTime toDate,
        int pageNumber,
        int resultsPerPage,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            request = new
            {
                SearchTerm = "",
                DateField = "processed",
                FromDate = fromDate,
                ToDate = toDate,
                PageNumber = pageNumber,
                ResultsPerPage = resultsPerPage
            }
        };
        return PostJsonAsync<SearchProcessedOrdersResponse>(
            "ProcessedOrders/SearchProcessedOrders",
            payload,
            cancellationToken);
    }

    public Task<GenericPagedResult<StockItem>> GetStockItemsAsync(
        Guid locationId,
        int pageNumber,
        int entriesPerPage,
        CancellationToken cancellationToken)
    {
        var body =
            $"keyWord=" +
            $"&locationId={Uri.EscapeDataString(JsonSerializer.Serialize(locationId))}" +
            $"&entriesPerPage={entriesPerPage}" +
            $"&pageNumber={pageNumber}" +
            $"&excludeComposites=true";
        return PostFormAsync<GenericPagedResult<StockItem>>("Stock/GetStockItems", body, cancellationToken);
    }

    public Task<Guid> CreatePurchaseOrderInitialAsync(
        CreatePurchaseOrderInitialParameter parameter,
        CancellationToken cancellationToken)
    {
        var body = "createParameters=" + Uri.EscapeDataString(SerializeForForm(parameter));
        return PostFormAsync<Guid>("PurchaseOrder/Create_PurchaseOrder_Initial", body, cancellationToken);
    }

    public Task<UpdatePurchaseOrderItemResponse> AddPurchaseOrderItemAsync(
        AddPurchaseOrderItemParameter parameter,
        CancellationToken cancellationToken)
    {
        var body = "addItemParameter=" + Uri.EscapeDataString(SerializeForForm(parameter));
        return PostFormAsync<UpdatePurchaseOrderItemResponse>("PurchaseOrder/Add_PurchaseOrderItem", body, cancellationToken);
    }

    public Task ChangePurchaseOrderStatusAsync(
        ChangePurchaseOrderStatusParameter parameter,
        CancellationToken cancellationToken)
    {
        var body = "changeStatusParameter=" + Uri.EscapeDataString(SerializeForForm(parameter));
        return PostFormAsync("PurchaseOrder/Change_PurchaseOrderStatus", body, cancellationToken);
    }

    public Task DeliverPurchaseItemAllAsync(Guid purchaseId, CancellationToken cancellationToken)
    {
        var body = "purchaseId=" + Uri.EscapeDataString(purchaseId.ToString());
        return PostFormAsync("PurchaseOrder/Deliver_PurchaseItemAll", body, cancellationToken);
    }

    public Task<List<Guid>> CreateOrdersAsync(
        IReadOnlyList<ChannelOrder> orders,
        string locationName,
        CancellationToken cancellationToken)
    {
        var body =
            "orders=" + Uri.EscapeDataString(SerializeForForm(orders)) +
            "&location=" + Uri.EscapeDataString(locationName);
        return PostFormAsync<List<Guid>>("Orders/CreateOrders", body, cancellationToken);
    }

    public Task<List<Guid>> SetInvoicesPrintedAsync(
        IReadOnlyList<Guid> orderIds,
        CancellationToken cancellationToken)
    {
        // Matches LinnworkAPI Factory form body: orderIds=[...]
        var body = "orderIds=" + Uri.EscapeDataString(SerializeForForm(orderIds));
        return PostFormAsync<List<Guid>>("Orders/SetInvoicesPrinted", body, cancellationToken);
    }

    public Task<List<Guid>> SetLabelsPrintedAsync(
        IReadOnlyList<Guid> orderIds,
        CancellationToken cancellationToken)
    {
        var body = "orderIds=" + Uri.EscapeDataString(SerializeForForm(orderIds));
        return PostFormAsync<List<Guid>>("Orders/SetLabelsPrinted", body, cancellationToken);
    }

    public Task<ProcessOrderResult> ProcessOrderAsync(
        Guid orderId,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            orderId,
            scanPerformed = true,
            locationId
        };
        return PostJsonAsync<ProcessOrderResult>("Orders/ProcessOrder", payload, cancellationToken);
    }

    public async Task SetOrderSubSourceAsync(
        Guid orderId,
        string subSource,
        CancellationToken cancellationToken)
    {
        var details = await GetOrderByIdAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (details.GeneralInfo.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidOperationException($"Order {orderId} did not include GeneralInfo.");
        }

        var info = JsonNode.Parse(details.GeneralInfo.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException($"Order {orderId} GeneralInfo was not an object.");
        info["SubSource"] = subSource;
        await PostJsonAllowEmptyAsync(
            "Orders/SetOrderGeneralInfo",
            new { orderId, info, wasDraft = false },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<OrderDetails> GetOrderByIdAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var body = "pkOrderId=" + Uri.EscapeDataString(orderId.ToString());
        return PostFormAsync<OrderDetails>("Orders/GetOrderById", body, cancellationToken);
    }

    public Task<CreateRefundResponse> CreateRefundAsync(
        CreateRefundRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new JsonRequestEnvelope<CreateRefundRequest> { Request = request };
        return PostJsonAsync<CreateRefundResponse>("ReturnsRefunds/CreateRefund", payload, cancellationToken);
    }

    public Task<ActionRefundResponse> ActionRefundAsync(
        ActionRefundRequest request,
        CancellationToken cancellationToken)
    {
        var payload = new JsonRequestEnvelope<ActionRefundRequest> { Request = request };
        return PostJsonAsync<ActionRefundResponse>("ReturnsRefunds/ActionRefund", payload, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string extension, CancellationToken cancellationToken)
    {
        var responseBody = await SendWithAuthRetryAsync(async (token, server, ct) =>
        {
            var client = _httpClientFactory.CreateClient(nameof(LinnworksClient));
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUrl(server, extension));
            request.Headers.TryAddWithoutValidation("Authorization", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger.LogDebug("GET {Extension}", extension);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var responseBodyLocal = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LinnworksHttpException(response.StatusCode, extension, responseBodyLocal);
            }

            return responseBodyLocal;
        }, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return default!;
        }

        return JsonSerializer.Deserialize<T>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize response for {extension}.");
    }

    private async Task PostFormAsync(string extension, string body, CancellationToken cancellationToken)
    {
        await SendFormAsync(extension, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> PostFormAsync<T>(string extension, string body, CancellationToken cancellationToken)
    {
        var responseBody = await SendFormAsync(extension, body, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return default!;
        }

        return JsonSerializer.Deserialize<T>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize response for {extension}.");
    }

    private async Task<string> SendFormAsync(string extension, string body, CancellationToken cancellationToken)
    {
        return await SendWithAuthRetryAsync(async (token, server, ct) =>
        {
            var client = _httpClientFactory.CreateClient(nameof(LinnworksClient));
            using var content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded");
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(server, extension))
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("Authorization", token);

            _logger.LogDebug("POST form {Extension}", extension);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LinnworksHttpException(response.StatusCode, extension, responseBody);
            }

            return responseBody;
        }, cancellationToken).ConfigureAwait(false);
    }

    private Task PostJsonAllowEmptyAsync(string extension, object payload, CancellationToken cancellationToken)
    {
        return SendWithAuthRetryAsync(async (token, server, ct) =>
        {
            var client = _httpClientFactory.CreateClient(nameof(LinnworksClient));
            var json = JsonSerializer.Serialize(payload, WriteJsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(server, extension))
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("Authorization", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger.LogDebug("POST json {Extension}", extension);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var responseBodyLocal = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LinnworksHttpException(response.StatusCode, extension, responseBodyLocal);
            }

            return responseBodyLocal;
        }, cancellationToken);
    }

    private async Task<T> PostJsonAsync<T>(string extension, object payload, CancellationToken cancellationToken)
    {
        var responseBody = await SendWithAuthRetryAsync(async (token, server, ct) =>
        {
            var client = _httpClientFactory.CreateClient(nameof(LinnworksClient));
            var json = JsonSerializer.Serialize(payload, WriteJsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(server, extension))
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("Authorization", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _logger.LogDebug("POST json {Extension}", extension);
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var responseBodyLocal = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LinnworksHttpException(response.StatusCode, extension, responseBodyLocal);
            }

            return responseBodyLocal;
        }, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<T>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize response for {extension}.");
    }

    private async Task<string> SendWithAuthRetryAsync(
        Func<string, string, CancellationToken, Task<string>> send,
        CancellationToken cancellationToken)
    {
        var (token, server) = await _authClient.GetSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await send(token, server, cancellationToken).ConfigureAwait(false);
        }
        catch (LinnworksHttpException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Session rejected for {Extension}. Re-authorizing.", ex.Extension);
            (token, server) = await _authClient.RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
            return await send(token, server, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string BuildUrl(string server, string extension)
    {
        var baseUrl = server.TrimEnd('/');
        if (!baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl += "/api";
        }

        return $"{baseUrl}/{extension.TrimStart('/')}";
    }

    private static string SerializeForForm(object value)
    {
        return JsonSerializer.Serialize(value, WriteJsonOptions);
    }
}

public sealed class LinnworksHttpException : Exception
{
    public LinnworksHttpException(HttpStatusCode statusCode, string extension, string responseBody)
        : base($"{extension} failed ({(int)statusCode}): {responseBody}")
    {
        StatusCode = statusCode;
        Extension = extension;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }
    public string Extension { get; }
    public string ResponseBody { get; }
}
