using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace EasyOcrSharp.Models;

/// <summary>
/// JSON settings shared by the library's <c>ToJson()</c> exporters.
/// </summary>
public static class EasyOcrJson
{
    /// <summary>
    /// The encoder the <c>ToJson()</c> exporters use by default. It emits the whole Basic Multilingual Plane
    /// verbatim, so recognized text in Cyrillic, Greek, Arabic, Hebrew, CJK, Devanagari… stays human-readable
    /// in Notepad and friends instead of collapsing into <c>\uXXXX</c> escapes (the System.Text.Json default,
    /// which only leaves ASCII unescaped). HTML-sensitive characters (<c>&lt; &gt; &amp; ' +</c>) are still
    /// escaped — unlike <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> — because block payloads
    /// such as <c>TableHtml</c> routinely carry markup that may be embedded in a page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output is UTF-8 JSON either way; escaping only changes readability, never the decoded text.
    /// </para>
    /// <para>
    /// <b>Scope.</b> <see cref="UnicodeRanges.All"/> is the Basic Multilingual Plane (U+0000–U+FFFF), so
    /// supplementary-plane scalars — emoji, and CJK Unified Ideographs Extension B and beyond (U+20000+,
    /// which turn up in Japanese and Chinese proper names) — are still written as a surrogate escape pair
    /// such as <c>𠮷</c>. That is valid JSON and deserializes back to the identical string; only
    /// the on-screen readability of those particular characters differs. A caller who needs them verbatim,
    /// and who is not embedding the JSON in an HTML page, can pass
    /// <c>ToJson(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })</c>.
    /// </para>
    /// </remarks>
    public static JavaScriptEncoder Encoder { get; } = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>
    /// Builds a fresh options instance carrying the exporter defaults (<see cref="Encoder"/> plus the
    /// requested indentation). Each source-generated context needs its own instance — a context takes
    /// ownership of the options it is constructed from — so this is a factory, not a shared singleton.
    /// </summary>
    internal static JsonSerializerOptions Options(bool indented)
        => new() { Encoder = Encoder, WriteIndented = indented };

    /// <summary>
    /// Returns a mutable, unbound copy of caller-supplied options, safe to hand to a source-generated
    /// <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> constructor. Copying keeps the
    /// caller's instance reusable (a context takes ownership of the options it is built from and seals
    /// them), and clearing the resolver lets the context install itself as the AOT-safe one.
    /// </summary>
    internal static JsonSerializerOptions ForContext(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new JsonSerializerOptions(options) { TypeInfoResolver = null };
    }
}
