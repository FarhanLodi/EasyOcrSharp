using System.Text.RegularExpressions;

namespace EasyOcrSharp.Extraction;

/// <summary>
/// Ready-made <see cref="FieldDefinition"/>s for the documents everyone starts with — invoices, bills and
/// receipts — plus the value patterns behind them, so your own definitions can reuse the same
/// amount/date/identifier matching instead of re-deriving it.
/// </summary>
/// <remarks>
/// <para>
/// The point of these is to get from an <see cref="Models.OcrResult"/> to structured fields in one line:
/// <code>
/// var fields = result.ExtractFieldValues(FieldPresets.Invoice);
/// string? total = fields.GetValueOrDefault("Total");
/// </code>
/// They are deliberately conservative: every money field carries an amount pattern and every date field a
/// date pattern, so a mis-anchored label yields nothing rather than nonsense. They are also plain records
/// — copy one with <c>with</c> to bend it to your layout (add an anchor in your language, widen
/// <see cref="FieldDefinition.MaxDistance"/>, switch <see cref="FieldDefinition.Occurrence"/>).
/// </para>
/// <para>
/// All patterns are <c>[GeneratedRegex]</c>-compiled at build time, so they cost no start-up reflection
/// and stay Native-AOT clean.
/// </para>
/// </remarks>
public static partial class FieldPresets
{
    /// <summary>
    /// Matches a money amount with optional sign, optional leading currency symbol, thousands separators
    /// (<c>,</c>, <c>.</c> or space) and up to two decimals — <c>42.00</c>, <c>$1,234.56</c>,
    /// <c>1 234,56</c>, <c>-99</c>. The <c>value</c> group holds the amount.
    /// </summary>
    public static Regex Amount { get; } = AmountRegex();

    /// <summary>
    /// Matches the common written date shapes: numeric (<c>2024-01-05</c>, <c>05/01/2024</c>,
    /// <c>5.1.24</c>) and month-name forms in either order (<c>5 Jan 2024</c>, <c>January 5, 2024</c>).
    /// English month names only. The <c>value</c> group holds the date exactly as written.
    /// </summary>
    public static Regex Date { get; } = DateRegex();

    /// <summary>
    /// Matches a document identifier — at least three characters of letters, digits, <c>-</c>, <c>/</c> or
    /// <c>_</c>, and containing at least one digit, which is what keeps a stray word like "Date" out of an
    /// invoice-number field. The <c>value</c> group holds the identifier.
    /// </summary>
    public static Regex Identifier { get; } = IdentifierRegex();

    /// <summary>
    /// Matches an ISO-4217 code for a widely used currency, or a currency symbol
    /// (<c>$ € £ ¥ ₹</c>). The <c>value</c> group holds the code or symbol.
    /// </summary>
    public static Regex Currency { get; } = CurrencyRegex();

    /// <summary>
    /// The invoice/bill number. Anchored on the multi-word labels only ("Invoice Number", "Invoice No",
    /// "Bill No", …) — a bare "Invoice" anchor would happily swallow the invoice <em>date</em> — and
    /// constrained to <see cref="Identifier"/>. Takes the first occurrence, since the number is normally
    /// printed in the header.
    /// </summary>
    public static FieldDefinition InvoiceNumber { get; } = new()
    {
        Name = "InvoiceNumber",
        Anchors = new[]
        {
            "Invoice Number", "Invoice No", "Invoice Num", "Invoice ID", "Invoice Ref",
            "Bill Number", "Bill No", "Document Number", "Document No", "Receipt Number", "Receipt No",
        },
        ValuePattern = Identifier,
        Occurrence = FieldOccurrence.First,
        MaxDistance = 8.0,
    };

    /// <summary>
    /// The purchase-order number referenced by the document ("PO Number", "Purchase Order", "Order No").
    /// Constrained to <see cref="Identifier"/>.
    /// </summary>
    public static FieldDefinition PurchaseOrderNumber { get; } = new()
    {
        Name = "PurchaseOrderNumber",
        Anchors = new[] { "Purchase Order", "PO Number", "PO No", "Order Number", "Order No", "Order ID" },
        ValuePattern = Identifier,
        Occurrence = FieldOccurrence.First,
        MaxDistance = 8.0,
    };

    /// <summary>
    /// The date the document was issued. The bare "Date" anchor is included because receipts often print
    /// nothing else; the <see cref="Date"/> pattern keeps it honest, and
    /// <see cref="FieldOccurrence.First"/> prefers the issue date printed above any due date.
    /// </summary>
    public static FieldDefinition InvoiceDate { get; } = new()
    {
        Name = "InvoiceDate",
        Anchors = new[] { "Invoice Date", "Date of Issue", "Issue Date", "Issued On", "Bill Date", "Date" },
        ValuePattern = Date,
        Occurrence = FieldOccurrence.First,
        MaxDistance = 8.0,
    };

    /// <summary>The date payment is due ("Due Date", "Payment Due", "Pay By"). Constrained to <see cref="Date"/>.</summary>
    public static FieldDefinition DueDate { get; } = new()
    {
        Name = "DueDate",
        Anchors = new[] { "Due Date", "Payment Due", "Date Due", "Pay By", "Payable By" },
        ValuePattern = Date,
        Occurrence = FieldOccurrence.First,
        MaxDistance = 8.0,
    };

    /// <summary>
    /// The payable total. Takes the <em>last</em> occurrence: invoices repeat "Total" per section and the
    /// figure that matters is the one at the bottom of the summary. Constrained to <see cref="Amount"/>.
    /// Note that "Subtotal" is far enough from "Total" not to be confused with it by the fuzzy matcher.
    /// </summary>
    public static FieldDefinition Total { get; } = new()
    {
        Name = "Total",
        Anchors = new[]
        {
            "Total", "Total Due", "Total Amount", "Amount Due", "Balance Due", "Grand Total",
            "Amount Payable", "Total Payable", "Amount Paid",
        },
        ValuePattern = Amount,
        Occurrence = FieldOccurrence.Last,
    };

    /// <summary>The pre-tax subtotal ("Subtotal", "Sub Total", "Net Amount"). Constrained to <see cref="Amount"/>.</summary>
    public static FieldDefinition Subtotal { get; } = new()
    {
        Name = "Subtotal",
        Anchors = new[] { "Subtotal", "Sub Total", "Net Amount", "Net Total", "Amount Excl Tax" },
        ValuePattern = Amount,
    };

    /// <summary>
    /// The tax charged ("Tax", "VAT", "GST", "Sales Tax"). Short labels are matched all but exactly — at
    /// the default similarity a three-letter anchor tolerates no error at all — so "Fax" cannot become
    /// "Tax". Constrained to <see cref="Amount"/>.
    /// </summary>
    public static FieldDefinition Tax { get; } = new()
    {
        Name = "Tax",
        Anchors = new[] { "Tax", "VAT", "GST", "Sales Tax", "Tax Amount", "Tax Total", "HST" },
        ValuePattern = Amount,
    };

    /// <summary>
    /// The document's currency, either from an explicit "Currency" label or from a symbol/code printed
    /// beside the total. Constrained to <see cref="Currency"/>.
    /// </summary>
    public static FieldDefinition CurrencyField { get; } = new()
    {
        Name = "Currency",
        Anchors = new[] { "Currency", "Currency Code", "Paid In", "Total", "Amount Due" },
        ValuePattern = Currency,
        Occurrence = FieldOccurrence.Last,
    };

    /// <summary>
    /// The whole invoice bundle: number, purchase order, issue date, due date, subtotal, tax, total and
    /// currency. Pass it straight to
    /// <see cref="FieldExtractionExtensions.ExtractFieldValues(Models.OcrResult, IEnumerable{FieldDefinition}, FieldExtractionOptions?)"/>.
    /// </summary>
    public static IReadOnlyList<FieldDefinition> Invoice { get; } = new[]
    {
        InvoiceNumber, PurchaseOrderNumber, InvoiceDate, DueDate, Subtotal, Tax, Total, CurrencyField,
    };

    /// <summary>
    /// The receipt subset — date, subtotal, tax, total and currency. Receipts rarely carry a document
    /// number or a due date, and asking for them only invites false positives.
    /// </summary>
    public static IReadOnlyList<FieldDefinition> Receipt { get; } = new[]
    {
        InvoiceDate, Subtotal, Tax, Total, CurrencyField,
    };

    // Patterns are single literals (not concatenations) so the regex source generator sees exactly what
    // is written here; the currency class carries the symbols themselves, in UTF-8.
    [GeneratedRegex(@"(?<value>[-+]?\s?[$€£¥₹]?\s?\d+(?:[., ]\d{3})*(?:[.,]\d{1,2})?)", RegexOptions.CultureInvariant)]
    private static partial Regex AmountRegex();

    [GeneratedRegex(@"(?<value>\d{1,4}[-/.]\d{1,2}[-/.]\d{2,4}|\d{1,2}(?:st|nd|rd|th)?\s*(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?,?\s*\d{2,4}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s*\d{1,2}(?:st|nd|rd|th)?,?\s*\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(?<value>(?=[A-Za-z0-9][-A-Za-z0-9/_]*\d)[A-Za-z0-9][-A-Za-z0-9/_]{2,})", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(?<value>USD|EUR|GBP|JPY|INR|CAD|AUD|CHF|CNY|SEK|NZD|SGD|AED|ZAR|[$€£¥₹])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyRegex();
}
