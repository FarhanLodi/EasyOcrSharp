using System.Globalization;
using System.Text;
using EasyOcrSharp.Evaluation;
using EasyImageSharp;
using EasyImageSharp.PixelFormats;
using EasyImageSharp.Processing;

namespace EasyOcrSharp.Tests;

/// <summary>
/// Ground truth and scoring helpers shared by the engine-comparison benchmark
/// (<see cref="EngineComparisonBenchmarkTests"/>) and the PP-OCRv5 integration tests. The references are
/// hand transcriptions of repository images — the full printed text where it is unambiguous, a bag of
/// printed words where a page mixes in handwriting or a collage has no single reading order — so both
/// engines are scored against the same text, never against each other.
/// </summary>
internal static class EngineBenchmarkData
{
    /// <summary>
    /// Finds a repository test asset: next to the test assembly first (the copied fixtures), then by
    /// walking up from it to the repository's <c>test/assets</c> (fixtures the project does not copy, such
    /// as <c>ocr_test1.png</c> / <c>ocr_test4.png</c> / <c>sample2.png</c>). Null when absent.
    /// </summary>
    public static string? RepoAsset(string name)
    {
        var copied = Path.Combine(AppContext.BaseDirectory, "assets", name);
        if (File.Exists(copied)) return copied;

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test", "assets", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- full-text references (CER / WER are meaningful) --------------------------------------------------

    /// <summary>A page with a full transcription: the asset, the languages it is read with, and the text.</summary>
    public sealed record FullTextSample(string Name, string Asset, string[] Languages, string Reference);

    /// <summary>
    /// Real and corpus pages with a complete, unambiguous transcription. The four corpus pages are the
    /// synthetic set's table, form, receipt and numbers/codes renders; <c>ocr_test1.png</c> is the classic
    /// six-language "eurotext" probe (punctuation, symbols, accented Latin).
    /// </summary>
    public static IReadOnlyList<FullTextSample> FullTextSamples { get; } =
    [
        new("sample", "sample.png", ["en"], "Hello World\nEasyOcrSharp 2024"),
        new("sample2", "sample2.png", ["en"],
            "The quick brown fox\njumps over the lazy dog.\nInvoice #12345 - $99.50\nOpenAI GPT-4 & Claude"),
        new("eurotext", "ocr_test1.png", ["en", "de", "fr", "it", "es", "pt"],
            "The (quick) [brown] {fox} jumps!\n" +
            "Over the $43,456.78 <lazy> #90 dog\n" +
            "& duck/goose, as 12.5% of E-mail\n" +
            "from aspammer@website.com is spam.\n" +
            "Der „schnelle” braune Fuchs springt\n" +
            "über den faulen Hund. Le renard brun\n" +
            "«rapide» saute par-dessus le chien\n" +
            "paresseux. La volpe marrone rapida\n" +
            "salta sopra il cane pigro. El zorro\n" +
            "marrón rápido salta sobre el perro\n" +
            "perezoso. A raposa marrom rápida\n" +
            "salta sobre o cão preguiçoso."),
        new("corpus_023_tables", "023_tables.png", ["en"],
            "OCR TEST — TABLES\n" +
            "ID Product Qty Unit Price Tax Status Total\n" +
            "1000 Module 28 610.59 1% PAID 7650.24\n" +
            "1001 Sensor 73 350.24 17% PAID 250.14\n" +
            "1002 Cable-X 55 99.10 11% PAID 6696.39\n" +
            "1003 Panel 46 399.95 12% PAID 3280.78\n" +
            "1004 Panel 10 91.06 15% DONE 4407.58\n" +
            "1005 Cable-X 38 42.80 7% DONE 1760.86\n" +
            "1006 Module 34 46.51 3% HOLD 8832.82\n" +
            "1007 Sensor 35 455.98 1% PAID 8509.12"),
        new("corpus_037_forms", "037_forms.png", ["en"],
            "OCR TEST — FORMS\n" +
            "Full Name: test@example.com\n" +
            "Company: Ahmedabad, Gujarat\n" +
            "Address: Farhan Lodi\n" +
            "City: Farhan Lodi\n" +
            "Postal Code: +91 98765 43210\n" +
            "Phone: 380001\n" +
            "Email: +91 98765 43210\n" +
            "Reference ID: +91 98765 43210\n" +
            "Comments: 380001"),
        new("corpus_057_receipts", "057_receipts_invoices.png", ["en"],
            "OCR TEST — RECEIPTS INVOICES\n" +
            "NOVA MART — TAX INVOICE\n" +
            "Invoice: INV-2048\n" +
            "Date: 01-09-2026\n" +
            "Customer: WALK-IN CUSTOMER\n" +
            "GSTIN: 24ABCDE1234F1Z5\n" +
            "USB-C Cable 2 x 399.00\n" +
            "Keyboard 1 x 1299.00\n" +
            "Mouse 1 x 799.00\n" +
            "Adapter 2 x 599.50\n" +
            "Service 1 x 499.00\n" +
            "SUBTOTAL: ₹ 4,595.00\n" +
            "CGST 9%: ₹ 413.55 SGST 9%: ₹ 413.55\n" +
            "GRAND TOTAL: ₹ 5,422.10"),
        new("corpus_096_numbers", "096_numbers_codes.png", ["en"],
            "OCR TEST — NUMBERS CODES\n" +
            "203214 AB-7359 77,152.07 A1B2C3D4\n" +
            "302225 ZX-5302 74,443.39 2026/09/01\n" +
            "327601 SKU-6489 57,036.44 +91-98765-43210\n" +
            "215581 Q7-4972 37,905.49 A1B2C3D4\n" +
            "268116 Q7-5227 30,560.33 2026/09/01\n" +
            "903855 INV-2559 21,572.65 2026/09/01\n" +
            "383287 AB-2845 84,605.57 2026/09/01\n" +
            "123967 Q7-6335 75,925.65 +91-98765-43210\n" +
            "742720 SKU-9700 51,088.24 A1B2C3D4\n" +
            "293781 Q7-5569 81,695.92 2026/09/01\n" +
            "734923 ZX-5460 35,928.26 +91-98765-43210\n" +
            "727206 ZX-1374 68,145.70 2026/09/01\n" +
            "704567 ZX-2284 60,410.67 A1B2C3D4"),
    ];

    // ---- bag-of-words references (order-free recall) -------------------------------------------------------

    /// <summary>A page scored by printed-word recall only (collages, or printed text mixed with handwriting).</summary>
    public sealed record WordBagSample(string Name, string Asset, string[] Languages, string Words);

    public static IReadOnlyList<WordBagSample> WordBagSamples { get; } =
    [
        // Nine real-photo panels (street sign, receipt, warning sign, business card, shipping label, road
        // sign, LCD, licence plate) — no single reading order exists across the collage.
        new("signs_collage", "ocr_test3.png", ["en"],
            "27 Maple Street SUNRISE MART 123 Greenfield Road Springfield, IL 62704 (217) 555-0198 " +
            "Date: 05/20/2024 10:35 AM Receipt #: 012345 Milk 1L $1.89 Bread $2.49 Eggs (12) $3.29 " +
            "Bananas $1.49 Total $9.16 Cash $10.00 Change $0.84 Thank you for shopping! Have a nice day! " +
            "DANGER HIGH VOLTAGE KEEP OUT authorized personnel only ABCD SOLUTIONS Innovate. Integrate. Inspire. " +
            "John Michael Project Manager +1 555 123 4567 john.michael@abcdsolutions.com www.abcdsolutions.com " +
            "456 Oak Avenue, Suite 200 Denver, CO 80202, USA FROM: Global Tech Ltd. 123 Industry Way " +
            "San Jose, CA 95131 USA TO: Alex Johnson 789 Pine Road London, SW1A 1AA United Kingdom " +
            "ORDER NO: GTX-2024-5678 DATE: 05/18/2024 TRACKING NO: 1Z 999 AA1 01 2345 6784 " +
            "Downtown 2 km Airport 15 km Maple Grove 23 km 23.5°C 54% WASHINGTON 11 WA 2024 BDF1234 EVERGREEN STATE"),
        // A scanned 1990s form (FUNSD-style); only the printed text is referenced — the handwritten notes
        // are left out, so this is recall over the printed words.
        new("scanned_form_printed", "structure_sample.png", ["en"],
            "QUALITY IMPROVEMENT SUGGESTION OUR MISSION IS SUPERIOR CONSUMER SATISFACTION " +
            "Highest Quality, Teamwork, Do Right Things Right The First Time RESEARCH & DEVELOPMENT " +
            "DATE: June 21, 1993 DEPARTMENT: R&D Library SUBMITTED BY: Carol S. Lincoln SUBMITTER'S SS#: 407-64-3484 " +
            "Quality Coord Only Date Rec'd QIP Log #1 Type Status (1993) Keywords (1993) " +
            "SUGGESTION: (Describe Current Situation and Idea) The current system of managing records is too complex. " +
            "The trend seems to be increasingly specific, when we should be getting more general. " +
            "Right now, people must work to understand the system. We must spend too much time administering " +
            "the system, labeling, and cleaning our files. Complying is a real burden, both for the individual " +
            "and for the records coordinators. Describe Possible Solutions and Benefits: " +
            "1. Drop the category specifications altogether. 2. Use more general categories. " +
            "PROFIT IMPROVEMENT: (If Applicable) RESPOND HERE Implement: Yes No Pending Date " +
            "Approval Signature/Date (Sign & Print Names) Responder/Date (See Instructions On Back)"),
    ];

    // ---- per-script real sign panels -----------------------------------------------------------------------

    /// <summary>
    /// One sign panel of <c>ocr_test4.png</c> (a 4 × 5 collage of real multilingual signs), cropped by its
    /// grid cell. <paramref name="EasyOcrLanguages"/> is null where EasyOCR has no recognizer for the
    /// script (Greek), so only PP-OCRv5 is scored there.
    /// </summary>
    public sealed record SignPanel(
        string Name, string Script, int Row, int Column, string[]? EasyOcrLanguages, string[] PpOcrLanguages, string Reference);

    /// <summary>
    /// Row boundaries of the collage as fractions of its height (the rows are not equally tall); the four
    /// columns are equal quarters.
    /// </summary>
    private static readonly double[] RowEdges = { 0.0, 0.2148, 0.4150, 0.6152, 0.8057, 1.0 };

    public static IReadOnlyList<SignPanel> SignPanels { get; } =
    [
        new("zh_street", "Han", 0, 0, ["ch_sim", "en"], ["ch_sim", "en"], "南 88 中山路 北 88 Zhongshan Rd. S N"),
        new("ja_station", "Kana+Han", 0, 1, ["ja", "en"], ["ja", "en"], "しんじゅく 新宿 Shinjuku よよぎ しぶや Yoyogi Shibuya"),
        new("ko_notice", "Hangul", 0, 2, ["ko"], ["ko"], "주의 관계자 외 출입금지 협조해 주셔서 감사합니다"),
        new("ru_exit", "Cyrillic", 0, 3, ["ru", "en"], ["ru", "en"], "ВЫХОД EXIT"),
        new("hi_no_smoking", "Devanagari", 1, 0, ["hi"], ["hi"], "धूम्रपान निषेध यहाँ धूम्रपान करना सख्त मना है।"),
        new("fr_street", "Latin", 1, 1, ["fr"], ["fr"], "RUE DE LA PAIX 2e ARRt"),
        new("de_caution", "Latin", 1, 2, ["de"], ["de"], "Vorsicht! Rutschgefahr bei Nässe"),
        new("es_bakery", "Latin", 1, 3, ["es"], ["es"], "PANADERÍA LA ESPIGA Pan fresco cada día"),
        new("ar_notice", "Arabic", 2, 0, ["ar"], ["ar"], "ملاحظة الرجاء إغلاق الباب بهدوء شكراً لك"),
        new("th_no_parking", "Thai", 2, 1, ["th"], ["th"], "ห้ามจอดรถ ตลอดเวลา"),
        // PP-OCRv5 refuses "vi" (its Latin dictionary has no tone-marked vowels); its Latin pack is read via "fr"
        // to show what it would return.
        new("vi_staff_only", "Latin (Vietnamese)", 2, 2, ["vi"], ["fr"], "KHÔNG PHẬN SỰ MIỄN VÀO Xin cảm ơn!"),
        new("ru_cctv", "Cyrillic", 2, 3, ["ru"], ["ru"], "ВНИМАНИЕ! ВЕДЁТСЯ ВИДЕОНАБЛЮДЕНИЕ"),
        new("zh_menu", "Han", 3, 0, ["ch_sim"], ["ch_sim"], "今日菜单 红烧牛肉面 ¥28 宫保鸡丁 ¥26 清炒时蔬 ¥18 米饭 ¥3"),
        new("pt_door", "Latin", 3, 1, ["pt"], ["pt"], "ATENÇÃO MANTENHA A PORTA FECHADA Obrigado pela colaboração!"),
        new("tr_litter", "Latin (Turkish)", 3, 2, ["tr"], ["tr"],
            "LÜTFEN ÇÖPLERİNİZİ ÇÖP KUTUSUNA ATINIZ. TEMİZ BİR ÇEVRE İÇİN TEŞEKKÜR EDERİZ."),
        new("pl_no_smoking", "Latin (Polish)", 3, 3, ["pl"], ["pl"], "ZAKAZ PALENIA NA TERENIE CAŁEGO OBIEKTU"),
        new("en_toilet", "Latin", 4, 0, ["en"], ["en"], "Toilet"),
        new("it_no_entry", "Latin", 4, 1, ["it"], ["it"], "VIETATO L’INGRESSO ai non autorizzati"),
        new("el_no_smoking", "Greek", 4, 2, null, ["el"], "ΑΠΑΓΟΡΕΥΕΤΑΙ ΤΟ ΚΑΠΝΙΣΜΑ"),
        new("en_keep_clean", "Latin", 4, 3, ["en"], ["en"], "PLEASE KEEP THIS AREA CLEAN"),
    ];

    /// <summary>Crops one sign panel out of the collage (the caller owns the result).</summary>
    public static Image<Rgb24> CropPanel(Image<Rgb24> collage, SignPanel panel)
    {
        int x0 = (int)Math.Round(collage.Width * panel.Column / 4.0);
        int x1 = (int)Math.Round(collage.Width * (panel.Column + 1) / 4.0);
        int y0 = (int)Math.Round(collage.Height * RowEdges[panel.Row]);
        int y1 = (int)Math.Round(collage.Height * RowEdges[panel.Row + 1]);
        return collage.Clone(ctx => ctx.Crop(new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0))));
    }

    // ---- scoring -------------------------------------------------------------------------------------------

    /// <summary>
    /// Case-, whitespace- and punctuation-insensitive comparison (NFKC) — the fair view across engines whose
    /// dictionaries spell quotes, dashes and the like differently.
    /// </summary>
    public static TextComparisonOptions Relaxed => TextComparisonOptions.Relaxed;

    /// <summary>
    /// CER/WER under <see cref="Relaxed"/>. For right-to-left references the hypothesis is also compared
    /// against the per-line character reversal of the reference and the better score is kept: both engines
    /// report Arabic in display order (EasyOCR's <c>get_display</c>), the transcription is in logical order.
    /// </summary>
    public static TextComparisonReport Compare(string reference, string hypothesis, bool rightToLeft = false)
    {
        var report = TextAccuracyMetrics.Compare(reference, hypothesis, Relaxed);
        if (!rightToLeft) return report;

        var reversed = string.Join("\n", reference.Split('\n').Select(line => new string(line.Reverse().ToArray())));
        var alternative = TextAccuracyMetrics.Compare(reversed, hypothesis, Relaxed);
        return alternative.CharacterErrorRate < report.CharacterErrorRate ? alternative : report;
    }

    /// <summary>
    /// Order-free character F1: the multiset overlap of the letters, digits and combining marks of the
    /// reference and the hypothesis (NFKC, case-folded). Insensitive to reading order, line splits and
    /// display-versus-logical RTL order — the robust per-script signal on multi-block signs.
    /// </summary>
    public static double CharBagF1(string reference, string hypothesis)
    {
        var expected = CharBag(reference);
        var actual = CharBag(hypothesis);
        int refCount = expected.Values.Sum();
        int hypCount = actual.Values.Sum();
        if (refCount == 0) return hypCount == 0 ? 1.0 : 0.0;
        if (hypCount == 0) return 0.0;

        int overlap = expected.Sum(kv => Math.Min(kv.Value, actual.GetValueOrDefault(kv.Key)));
        double precision = overlap / (double)hypCount;
        double recall = overlap / (double)refCount;
        return precision + recall == 0 ? 0.0 : 2 * precision * recall / (precision + recall);
    }

    /// <summary>
    /// Order-free word recall: the share of reference words (NFKC, case-folded, edge punctuation trimmed)
    /// that also occur in the hypothesis, counted as a multiset.
    /// </summary>
    public static double WordRecall(string reference, string hypothesis)
    {
        var expected = WordBag(reference);
        var actual = WordBag(hypothesis);
        int total = expected.Values.Sum();
        if (total == 0) return 1.0;
        int hits = expected.Sum(kv => Math.Min(kv.Value, actual.GetValueOrDefault(kv.Key)));
        return hits / (double)total;
    }

    private static Dictionary<string, int> CharBag(string text)
    {
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            bool keep = Rune.IsLetterOrDigit(rune)
                || category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            if (!keep) continue;
            var key = rune.ToString();
            bag[key] = bag.GetValueOrDefault(key) + 1;
        }
        return bag;
    }

    private static Dictionary<string, int> WordBag(string text)
    {
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
                     .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim().TrimStart(TrimmedPunctuation).TrimEnd(TrimmedPunctuation);
            if (word.Length == 0) continue;
            bag[word] = bag.GetValueOrDefault(word) + 1;
        }
        return bag;
    }

    private static readonly char[] TrimmedPunctuation =
        ".,;:!?\"'()[]{}<>«»“”„‘’-—–".ToCharArray();
}
