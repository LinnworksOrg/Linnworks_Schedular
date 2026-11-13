using System.Text.Json.Serialization;

namespace LinnworksDailySeed.Models;

public sealed class BaseSession
{
    public string? Token { get; set; }
    public string? Server { get; set; }
    public string? Email { get; set; }
    public string? UserId { get; set; }
}

public sealed class StockLocation
{
    public Guid StockLocationId { get; set; }
    public string? LocationName { get; set; }
    public bool IsFulfillmentCenter { get; set; }
    public string? LocationTag { get; set; }
}

public sealed class Supplier
{
    public Guid pkSupplierID { get; set; }
    public string? SupplierName { get; set; }
    public string? Currency { get; set; }
}

public sealed class PaymentMethod
{
    public string? Name { get; set; }
    public Guid PaymentMethodId { get; set; }
}

public sealed class ShippingMethod
{
    public string? Vendor { get; set; }
    public List<PostageService> PostalServices { get; set; } = new();
}

public sealed class PostageService
{
    public Guid pkPostalServiceId { get; set; }
    public string? PostalServiceName { get; set; }
    public string? Vendor { get; set; }
}

public sealed class GenericPagedResult<T>
{
    public List<T> Data { get; set; } = new();
    public int TotalEntries { get; set; }
    public int TotalPages { get; set; }
    public int PageNumber { get; set; }
    public int EntriesPerPage { get; set; }
}

public sealed class StockItem
{
    public string? ItemNumber { get; set; }
    public string? ItemTitle { get; set; }
    public double PurchasePrice { get; set; }
    public double? RetailPrice { get; set; }
    public int Quantity { get; set; }
    public double TaxRate { get; set; }
    public int Available { get; set; }
    public Guid StockItemId { get; set; }
}

public sealed class UpdatePurchaseOrderItemResponse
{
    public PurchaseOrderHeader? PurchaseOrderHeader { get; set; }

    /// <summary>
    /// Linnworks Add/Update_PurchaseOrderItem returns the line as NewPurchaseOrderItem.
    /// </summary>
    public PurchaseOrderItem? NewPurchaseOrderItem { get; set; }

    /// <summary>
    /// Some older payloads used PurchaseOrderItem for the same line object.
    /// </summary>
    public PurchaseOrderItem? PurchaseOrderItem { get; set; }

    public PurchaseOrderItem? ResolvedItem => NewPurchaseOrderItem ?? PurchaseOrderItem;
}

public sealed class PurchaseOrderResponse
{
    public PurchaseOrderHeader? PurchaseOrderHeader { get; set; }
    public List<PurchaseOrderItem> PurchaseOrderItem { get; set; } = new();
}

public sealed class PurchaseOrderHeader
{
    public Guid pkPurchaseID { get; set; }
    public string? ExternalInvoiceNumber { get; set; }
    public string? Status { get; set; }
}

public sealed class PurchaseOrderItem
{
    public Guid pkPurchaseItemId { get; set; }
    public Guid fkStockItemId { get; set; }
    public int Quantity { get; set; }
}

public sealed class ProcessOrderResult
{
    public Guid OrderId { get; set; }
    public bool Processed { get; set; }
    public string? Error { get; set; }
}

public sealed class OrderDetails
{
    public Guid OrderId { get; set; }
    public int NumOrderId { get; set; }
    public bool Processed { get; set; }
    public System.Text.Json.JsonElement GeneralInfo { get; set; }
    public List<OrderItem> Items { get; set; } = new();
}

public sealed class OrderItem
{
    public Guid RowId { get; set; }
    public string? SKU { get; set; }
    public string? ChannelSKU { get; set; }
    public string? Title { get; set; }
    public int Quantity { get; set; }
    [JsonPropertyName("Qty")]
    public int Qty
    {
        get => Quantity;
        set => Quantity = value;
    }
    public double PricePerUnit { get; set; }
    public double Cost { get; set; }
    public double CostIncTax { get; set; }
    public bool IsService { get; set; }
}

public sealed class ChannelOrder
{
    public bool UseChannelTax { get; set; } = true;
    public bool AutomaticallyLinkBySKU { get; set; } = true;
    public bool AutomaticallyLinkByBarcode { get; set; } = true;
    public bool AutomaticallyLinkByASIN { get; set; } = true;
    public string? MatchPostalServiceTag { get; set; }
    public string? PostalServiceName { get; set; }
    public bool SavePostalServiceIfNotExist { get; set; }
    public string? MatchPaymentMethodTag { get; set; }
    public string? PaymentMethodName { get; set; }
    public bool SavePaymentMethodIfNotExist { get; set; }
    public string? MappingSource { get; set; } = "DIRECT";
    public ChannelOrderState OrderState { get; set; } = ChannelOrderState.None;
    public ChannelOrderStatus OrderStatusType { get; set; } = ChannelOrderStatus.Unshipped;
    public string OrderStatus { get; set; } = "Unshipped";
    public ChannelPaymentStatus PaymentStatus { get; set; } = ChannelPaymentStatus.Paid;
    public List<ChannelOrderItem> OrderItems { get; set; } = new();
    public List<ChannelOrderNote> Notes { get; set; } = new();
    public string? Source { get; set; }
    public string? SubSource { get; set; }
    public string? ChannelBuyerName { get; set; }
    public string? ReferenceNumber { get; set; }
    public string? Currency { get; set; } = "GBP";
    public double ConversionRate { get; set; } = 1;
    public DateTime ReceivedDate { get; set; }
    public DateTime DispatchBy { get; set; }
    public DateTime? PaidOn { get; set; }
    public double? PostalServiceCost { get; set; }
    public double? PostalServiceTaxRate { get; set; }
    public double? PostalServiceDiscount { get; set; }
    public double? Discount { get; set; }
    public DiscountType? DiscountType { get; set; }
    public DiscountTaxType? DiscountTaxType { get; set; }
    public ChannelAddress? BillingAddress { get; set; }
    public ChannelAddress? DeliveryAddress { get; set; }
}

public enum DiscountType
{
    AllEvenly = 0,
    ItemsThenPostage = 1,
    PostageThenItems = 2
}

public enum DiscountTaxType
{
    DeductBeforeTax = 0,
    DeductAfterTax = 1
}

public enum ChannelOrderState
{
    None = 0,
    Hold = 1,
    Park = 2
}

public enum ChannelOrderStatus
{
    Unknown = 0,
    Pending = 1,
    Unshipped = 2,
    PartiallyShipped = 3,
    Shipped = 4,
    PartiallyCancelled = 5,
    Cancelled = 6,
    PartiallyRefunded = 7,
    Refunded = 8
}

public enum ChannelPaymentStatus
{
    Unpaid = 0,
    Paid = 1,
    Cancelled = 2
}

public sealed class ChannelOrderItem
{
    public bool TaxCostInclusive { get; set; } = true;
    public bool UseChannelTax { get; set; } = true;
    public double PricePerUnit { get; set; }
    public int Qty { get; set; }
    public double TaxRate { get; set; }
    public double LineDiscount { get; set; }
    public string? ItemNumber { get; set; }
    public string? ChannelSKU { get; set; }
    public bool IsService { get; set; }
    public string? ItemTitle { get; set; }
    public List<ChannelOrderItemTax>? Taxes { get; set; }
}

public sealed class ChannelOrderItemTax
{
    public string TaxType { get; set; } = "VAT";
    public double TaxValue { get; set; }
    public bool IsSellerCollected { get; set; } = true;
}

public sealed class ChannelOrderNote
{
    public string Note { get; set; } = "AUTO-SEED";
    public string NoteUserName { get; set; } = "LinnworksDailySeed";
}

public sealed class ChannelAddress
{
    public string FullName { get; set; } = "AUTO SEED Customer";
    public string Company { get; set; } = "AUTO-SEED";
    public string Address1 { get; set; } = "1 Seed Street";
    public string Address2 { get; set; } = "";
    public string Address3 { get; set; } = "";
    public string Town { get; set; } = "London";
    public string Region { get; set; } = "London";
    public string PostCode { get; set; } = "SW1A 1AA";
    public string Country { get; set; } = "United Kingdom";
    public string CountryCode { get; set; } = "GB";
    public string PhoneNumber { get; set; } = "0000000000";
    public string EmailAddress { get; set; } = "autoseed@example.com";
}

public sealed class CreateRefundRequest
{
    public bool ChannelInitiated { get; set; }
    public Guid OrderId { get; set; }
    public List<NewRefundLine> RefundLines { get; set; } = new();
}

public sealed class NewRefundLine
{
    public Guid? OrderItemRowId { get; set; }
    public string RefundedUnit { get; set; } = "Item";
    public string? FreeTextOrNote { get; set; }
    public double Amount { get; set; }
    public int Quantity { get; set; } = 1;
    public bool? IsFreeText { get; set; }
}

public sealed class CreateRefundResponse
{
    public int RefundHeaderId { get; set; }
    public string? RefundReference { get; set; }
    public string? CannotRefundReason { get; set; }
    public List<string> Errors { get; set; } = new();
    public PostSaleStatus? Status { get; set; }
}

public sealed class ActionRefundRequest
{
    public int RefundHeaderId { get; set; }
    public Guid OrderId { get; set; }
}

public sealed class ActionRefundResponse
{
    public bool SuccessfullyActioned { get; set; }
    public int RefundHeaderId { get; set; }
    public string? RefundReference { get; set; }
    public string? CannotRefundReason { get; set; }
    public List<string> Errors { get; set; } = new();
    public PostSaleStatus? Status { get; set; }
}

public sealed class PostSaleStatus
{
    public string? StatusHeader { get; set; }
}

public sealed class CreatePurchaseOrderInitialParameter
{
    public Guid fkSupplierId { get; set; }
    public Guid fkLocationId { get; set; }
    public DateTime DateOfPurchase { get; set; }
    public DateTime QuotedDeliveryDate { get; set; }
    public string ExternalInvoiceNumber { get; set; } = "";
    public double PostagePaid { get; set; }
    public string Currency { get; set; } = "GBP";
    public double ShippingTaxRate { get; set; }
    public double ConversionRate { get; set; } = 1;
    public string SupplierReferenceNumber { get; set; } = "";
}

public sealed class AddPurchaseOrderItemParameter
{
    public Guid pkPurchaseId { get; set; }
    public Guid fkStockItemId { get; set; }
    public int Qty { get; set; }
    public double Cost { get; set; }
    public double TaxRate { get; set; }
    public int PackQuantity { get; set; } = 1;
    public int PackSize { get; set; } = 1;
    public double Delivered { get; set; }
}

public sealed class ChangePurchaseOrderStatusParameter
{
    public Guid pkPurchaseId { get; set; }
    public string status { get; set; } = "OPEN";
}

public sealed class DeliverPurchaseItemParameter
{
    public Guid pkPurchaseId { get; set; }
    public Guid pkPurchaseItemId { get; set; }
    public int Delivered { get; set; }
    public string? BatchNumber { get; set; }
    public string? BinRack { get; set; }
    public int PrioritySequence { get; set; }
    public string? BatchStatus { get; set; }
}

public sealed class PrintTemplateHeader
{
    public int pkTemplateRowId { get; set; }
    public Guid TemplateId { get; set; }
    public string? TemplateType { get; set; }
    public string? TemplateName { get; set; }
    public bool Enabled { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class DeliverPurchaseOrderItemResponse
{
    public PurchaseOrderHeader? PurchaseOrderHeader { get; set; }
    public PurchaseOrderItem? NewPurchaseOrderItem { get; set; }
}

public sealed class GetAdditionalCostTypesResponse
{
    public List<AdditionalCostType> AdditionalTypes { get; set; } = new();
}

public sealed class AdditionalCostType
{
    public int AdditionalCostTypeId { get; set; }
    public string? TypeName { get; set; }
    public bool IsShippingType { get; set; }
    public bool IsPartialAllocation { get; set; }
    public bool Print { get; set; }
    public string? AllocationMethod { get; set; }
}

public sealed class AddAdditionalCostItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int AdditionalCostTypeId { get; set; }
    public string Reference { get; set; } = "";
    public double SubTotalLineCost { get; set; }
    public double TaxRate { get; set; }
    public string Currency { get; set; } = "GBP";
    public double ConversionRate { get; set; } = 1;
    public bool AllocationLocked { get; set; }
    public bool Print { get; set; }
    public string AllocationMethod { get; set; } = "ByValue";
}

public sealed class GetRefundOptionsRequest
{
    public Guid OrderId { get; set; }
    public int? RefundHeaderId { get; set; }
}

public sealed class RefundOptionsResponse
{
    public bool CanRefund { get; set; }
    public bool CanRefundAdditionally { get; set; }
    public bool CanRefundServices { get; set; }
    public bool CanRefundShipping { get; set; }
    public bool CanRefundShippingIndependently { get; set; }
    public bool CanRefundFreeText { get; set; }
}

public sealed class ReturnRowQty
{
    public Guid OrderItemRowId { get; set; }
    public int Qty { get; set; }
    public int Refund { get; set; }
    public string? ReturnItemSKU { get; set; }
    public string? ReturnItemTitle { get; set; }
}

public sealed class CurrencyConversionRate
{
    public string Currency { get; set; } = "";
    public double ConversionRate { get; set; }
}

public sealed class ChannelHeader
{
    public string? Source { get; set; }
    public string? SubSource { get; set; }
}

public sealed class CustomerAddress
{
    public string? EmailAddress { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? Town { get; set; }
    public string? Region { get; set; }
    public string? PostCode { get; set; }
    public string? Country { get; set; }
    public string? FullName { get; set; }
    public string? Company { get; set; }
    public string? PhoneNumber { get; set; }
}

public sealed class SearchProcessedOrdersResponse
{
    public GenericPagedResult<ProcessedOrderWeb>? ProcessedOrders { get; set; }
}

public sealed class ProcessedOrderWeb
{
    public string? cShippingAddress { get; set; }
    public string? cFullName { get; set; }
    public string? cEmailAddress { get; set; }
    public string? cCountry { get; set; }
    public string? cPostCode { get; set; }
    public string? Address1 { get; set; }
    public string? Address2 { get; set; }
    public string? Address3 { get; set; }
    public string? Town { get; set; }
    public string? Region { get; set; }
    public string? Company { get; set; }
    public string? BuyerPhoneNumber { get; set; }
    public string? ChannelBuyerName { get; set; }
    public string? BillingName { get; set; }
    public string? BillingCompany { get; set; }
    public string? BillingAddress1 { get; set; }
    public string? BillingAddress2 { get; set; }
    public string? BillingAddress3 { get; set; }
    public string? BillingTown { get; set; }
    public string? BillingRegion { get; set; }
    public string? BillingPostCode { get; set; }
    public string? BillingCountryName { get; set; }
    public string? BillingPhoneNumber { get; set; }
    public string? BillingEmailAddress { get; set; }
    public string? SubSource { get; set; }
    public string? cCurrency { get; set; }
}

public sealed class JsonRequestEnvelope<T>
{
    [JsonPropertyName("request")]
    public T Request { get; set; } = default!;
}
