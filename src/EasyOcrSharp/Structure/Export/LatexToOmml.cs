using System.Collections.Generic;
using System.Text;

namespace EasyOcrSharp.Structure.Export;

/// <summary>
/// Converts a LaTeX math string to an OMML <c>&lt;m:oMath&gt;</c> element. Best-effort: common math
/// (fractions, sub/superscripts, roots, Greek letters, operators, n-ary sum/integral) is converted to native
/// OMML; unsupported constructs degrade to literal text runs. Never throws.
/// </summary>
internal static class LatexToOmml
{
    // The OMML (Office Math Markup Language) namespace. Bound to the "m" prefix ON the returned root so the
    // result is self-contained and can be dropped straight into a WordprocessingML paragraph.
    private const string MathNs = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    // LaTeX control sequences that map to a single Unicode character emitted as an ordinary text run.
    // Greek letters (lower and the distinct upper-case forms) plus common operators/relations/symbols.
    private static readonly Dictionary<string, string> Symbols = new()
    {
        // Lower-case Greek.
        ["alpha"] = "α",   // α
        ["beta"] = "β",    // β
        ["gamma"] = "γ",   // γ
        ["delta"] = "δ",   // δ
        ["epsilon"] = "ε", // ε
        ["varepsilon"] = "ε",
        ["zeta"] = "ζ",    // ζ
        ["eta"] = "η",     // η
        ["theta"] = "θ",   // θ
        ["vartheta"] = "ϑ",
        ["iota"] = "ι",    // ι
        ["kappa"] = "κ",   // κ
        ["lambda"] = "λ",  // λ
        ["mu"] = "μ",      // μ
        ["nu"] = "ν",      // ν
        ["xi"] = "ξ",      // ξ
        ["omicron"] = "ο", // ο
        ["pi"] = "π",      // π
        ["varpi"] = "ϖ",
        ["rho"] = "ρ",     // ρ
        ["varrho"] = "ϱ",
        ["sigma"] = "σ",   // σ
        ["varsigma"] = "ς",
        ["tau"] = "τ",     // τ
        ["upsilon"] = "υ", // υ
        ["phi"] = "φ",     // φ
        ["varphi"] = "ϕ",
        ["chi"] = "χ",     // χ
        ["psi"] = "ψ",     // ψ
        ["omega"] = "ω",   // ω
        // Distinct upper-case Greek (the rest look identical to Latin letters and are left to authors).
        ["Gamma"] = "Γ",   // Γ
        ["Delta"] = "Δ",   // Δ
        ["Theta"] = "Θ",   // Θ
        ["Lambda"] = "Λ",  // Λ
        ["Xi"] = "Ξ",      // Ξ
        ["Pi"] = "Π",      // Π
        ["Sigma"] = "Σ",   // Σ
        ["Upsilon"] = "Υ", // Υ
        ["Phi"] = "Φ",     // Φ
        ["Psi"] = "Ψ",     // Ψ
        ["Omega"] = "Ω",   // Ω
        // Operators, relations and miscellaneous symbols.
        ["times"] = "×",      // ×
        ["cdot"] = "·",       // ·
        ["pm"] = "±",         // ±
        ["mp"] = "∓",         // ∓
        ["div"] = "÷",        // ÷
        ["ast"] = "∗",        // ∗
        ["star"] = "⋆",       // ⋆
        ["leq"] = "≤",        // ≤
        ["le"] = "≤",         // ≤
        ["geq"] = "≥",        // ≥
        ["ge"] = "≥",         // ≥
        ["neq"] = "≠",        // ≠
        ["ne"] = "≠",         // ≠
        ["approx"] = "≈",     // ≈
        ["equiv"] = "≡",      // ≡
        ["sim"] = "∼",        // ∼
        ["propto"] = "∝",     // ∝
        ["infty"] = "∞",      // ∞
        ["rightarrow"] = "→", // →
        ["to"] = "→",         // →
        ["leftarrow"] = "←",  // ←
        ["Rightarrow"] = "⇒", // ⇒
        ["Leftarrow"] = "⇐",  // ⇐
        ["leftrightarrow"] = "↔", // ↔
        ["mapsto"] = "↦",     // ↦
        ["partial"] = "∂",    // ∂
        ["nabla"] = "∇",      // ∇
        ["cdots"] = "⋯",      // ⋯
        ["ldots"] = "…",      // …
        ["dots"] = "…",       // …
        ["vdots"] = "⋮",      // ⋮
        ["ddots"] = "⋱",      // ⋱
        ["forall"] = "∀",     // ∀
        ["exists"] = "∃",     // ∃
        ["in"] = "∈",         // ∈
        ["notin"] = "∉",      // ∉
        ["subset"] = "⊂",     // ⊂
        ["supset"] = "⊃",     // ⊃
        ["subseteq"] = "⊆",   // ⊆
        ["supseteq"] = "⊇",   // ⊇
        ["cup"] = "∪",        // ∪
        ["cap"] = "∩",        // ∩
        ["emptyset"] = "∅",   // ∅
        ["angle"] = "∠",      // ∠
        ["perp"] = "⊥",       // ⊥
        ["parallel"] = "∥",   // ∥
        ["langle"] = "⟨",     // ⟨
        ["rangle"] = "⟩",     // ⟩
        ["lfloor"] = "⌊",     // ⌊
        ["rfloor"] = "⌋",     // ⌋
        ["lceil"] = "⌈",      // ⌈
        ["rceil"] = "⌉",      // ⌉
        ["wedge"] = "∧",      // ∧
        ["vee"] = "∨",        // ∨
        ["land"] = "∧",       // ∧
        ["lor"] = "∨",        // ∨
        ["neg"] = "¬",        // ¬
        ["oplus"] = "⊕",      // ⊕
        ["otimes"] = "⊗",     // ⊗
        ["circ"] = "∘",       // ∘
        ["bullet"] = "•",     // •
        ["prime"] = "′",      // ′
        ["degree"] = "°",     // °
        ["aleph"] = "ℵ",      // ℵ
        ["hbar"] = "ℏ",       // ℏ
        ["ell"] = "ℓ",        // ℓ
        ["Re"] = "ℜ",         // ℜ
        ["Im"] = "ℑ",         // ℑ
    };

    // n-ary operators handled specially (sum/integral/product and friends) with optional limits and an operand.
    private static readonly Dictionary<string, string> NaryChars = new()
    {
        ["sum"] = "∑",     // ∑
        ["int"] = "∫",     // ∫
        ["iint"] = "∬",    // ∬
        ["iiint"] = "∭",   // ∭
        ["oint"] = "∮",    // ∮
        ["prod"] = "∏",    // ∏
        ["coprod"] = "∐",  // ∐
        ["bigcup"] = "⋃",  // ⋃
        ["bigcap"] = "⋂",  // ⋂
        ["bigoplus"] = "⨁",// ⨁
        ["bigotimes"] = "⨂",// ⨂
    };

    // Control sequences that produce horizontal spacing only; collapsed/ignored.
    private static readonly HashSet<string> SpacingCommands = new()
    {
        ",", ";", ":", "!", " ", "quad", "qquad", "thinspace", "medspace", "thickspace", "negthinspace",
    };

    /// <summary>
    /// Converts <paramref name="latex"/> to a self-contained OMML <c>&lt;m:oMath&gt;…&lt;/m:oMath&gt;</c>
    /// element string (the <c>m:</c> prefix is bound to the OMML namespace ON the returned element, so it can
    /// be embedded directly in a WordprocessingML paragraph). Returns <see cref="string.Empty"/> for null/blank
    /// input. Never throws — on a parse failure the original text is emitted as a single escaped text run.
    /// </summary>
    public static string Convert(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
        {
            return string.Empty;
        }

        var body = new StringBuilder();
        try
        {
            var parser = new Parser(latex);
            parser.ParseInto(body, stopAtBrace: false);
        }
        catch
        {
            // Best-effort contract: on any failure fall back to the raw text as a single literal run so the
            // caller still gets a well-formed (if unstructured) equation rather than an exception.
            body.Clear();
            body.Append("<m:r><m:t>").Append(Xml(latex)).Append("</m:t></m:r>");
        }

        return $"<m:oMath xmlns:m=\"{MathNs}\">{body}</m:oMath>";
    }

    /// <summary>
    /// A small recursive-descent parser over the LaTeX source. It walks left to right, emitting OMML for each
    /// recognized construct and recursing into braced groups, fraction arguments, scripts and n-ary operands.
    /// </summary>
    private sealed class Parser
    {
        private readonly string _src;
        private int _pos;

        public Parser(string src) => _src = src;

        private bool End => _pos >= _src.Length;
        private char Peek => _src[_pos];

        /// <summary>
        /// Parses tokens into <paramref name="sb"/> until end of input (or, when <paramref name="stopAtBrace"/>
        /// is set, until the matching <c>}</c> which is consumed by the caller of a group).
        /// </summary>
        public void ParseInto(StringBuilder sb, bool stopAtBrace)
        {
            // Buffer of consecutive ordinary characters so they coalesce into a single <m:r> run.
            var run = new StringBuilder();

            void FlushRun()
            {
                if (run.Length > 0)
                {
                    // The run buffer holds raw source characters; escape XML-significant ones at emit time.
                    EmitRun(sb, Xml(run.ToString()));
                    run.Clear();
                }
            }

            while (!End)
            {
                char c = Peek;

                if (c == '}')
                {
                    if (stopAtBrace)
                    {
                        break; // Leave the '}' for the group reader to consume.
                    }

                    _pos++; // Stray closing brace: skip silently (best-effort).
                    continue;
                }

                if (c == '{')
                {
                    // A bare group. Parse it, then check for trailing scripts so "{..}^2" works.
                    FlushRun();
                    _pos++; // consume '{'
                    string group = ReadGroupBody();
                    EmitWithPossibleScripts(sb, group);
                    continue;
                }

                if (c == '^' || c == '_')
                {
                    // A script with no visible base (e.g. leading "^2"): use an empty base element.
                    FlushRun();
                    EmitWithPossibleScripts(sb, string.Empty, baseAlreadyConsumed: true);
                    continue;
                }

                if (c == '\\')
                {
                    FlushRun();
                    ParseCommand(sb);
                    continue;
                }

                // Ordinary character. Atoms that can carry a script (single char) are handled by peeking ahead.
                if (Following() is '^' or '_')
                {
                    // This char is the base of a script; render base + script as a unit. The base is ordinary
                    // text, so it is wrapped in a run (baseIsRawOmml stays false).
                    _pos++; // consume the base char
                    EmitWithPossibleScripts(sb, Xml(c.ToString()));
                    continue;
                }

                run.Append(c);
                _pos++;
            }

            FlushRun();
        }

        /// <summary>Returns the character one past the current position, or '\0' if at end.</summary>
        private char Following() => _pos + 1 < _src.Length ? _src[_pos + 1] : '\0';

        /// <summary>
        /// Reads the body of a brace group whose opening <c>{</c> has already been consumed, and consumes the
        /// matching <c>}</c>. Returns the OMML for the group's contents (no wrapper element).
        /// </summary>
        private string ReadGroupBody()
        {
            var inner = new StringBuilder();
            ParseInto(inner, stopAtBrace: true);
            if (!End && Peek == '}')
            {
                _pos++; // consume the matching '}'
            }
            return inner.ToString();
        }

        /// <summary>
        /// Reads a single "argument" following a command or script operator: a braced group, a backslash
        /// command, or a single character. Returns the OMML for that argument.
        /// </summary>
        private string ReadArgument()
        {
            SkipPlainSpaces();
            if (End)
            {
                return string.Empty;
            }

            char c = Peek;
            if (c == '{')
            {
                _pos++;
                return ReadGroupBody();
            }

            if (c == '\\')
            {
                var one = new StringBuilder();
                ParseCommand(one);
                return one.ToString();
            }

            // A single ordinary character argument.
            _pos++;
            return Xml(c.ToString());
        }

        /// <summary>Parses a <c>\command</c> beginning at the current backslash and appends its OMML to <paramref name="sb"/>.</summary>
        private void ParseCommand(StringBuilder sb)
        {
            _pos++; // consume '\'
            if (End)
            {
                return; // trailing backslash
            }

            char first = Peek;

            // Non-letter control sequences: "\,", "\;", "\!", "\ ", "\{", "\}", "\%", "\$", "\&", "\#", "\_".
            if (!char.IsLetter(first))
            {
                _pos++;
                string single = first.ToString();
                if (SpacingCommands.Contains(single))
                {
                    EmitRun(sb, " ");
                    return;
                }

                // Escaped literal punctuation -> emit the character itself (XML-escaped so "\&" etc. stay valid).
                EmitRun(sb, Xml(single));
                return;
            }

            string name = ReadCommandName();

            // Fractions.
            if (name == "frac" || name == "dfrac" || name == "tfrac" || name == "cfrac")
            {
                string num = ReadArgument();
                string den = ReadArgument();
                sb.Append("<m:f><m:fPr/><m:num>").Append(WrapOrEmpty(num))
                  .Append("</m:num><m:den>").Append(WrapOrEmpty(den)).Append("</m:den></m:f>");
                return;
            }

            // Binomials -> rendered as a stacked fraction without a bar is non-trivial; approximate as a fraction.
            if (name == "binom")
            {
                string top = ReadArgument();
                string bottom = ReadArgument();
                sb.Append("<m:f><m:fPr><m:type m:val=\"noBar\"/></m:fPr><m:num>").Append(WrapOrEmpty(top))
                  .Append("</m:num><m:den>").Append(WrapOrEmpty(bottom)).Append("</m:den></m:f>");
                return;
            }

            // Roots: \sqrt{x} or \sqrt[n]{x}.
            if (name == "sqrt")
            {
                string? degree = TryReadOptionalArgument();
                string radicand = ReadArgument();
                if (degree is null)
                {
                    sb.Append("<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr><m:deg/><m:e>")
                      .Append(WrapOrEmpty(radicand)).Append("</m:e></m:rad>");
                }
                else
                {
                    sb.Append("<m:rad><m:radPr/><m:deg>").Append(WrapOrEmpty(degree))
                      .Append("</m:deg><m:e>").Append(WrapOrEmpty(radicand)).Append("</m:e></m:rad>");
                }
                return;
            }

            // n-ary operators (sum/int/prod/...).
            if (NaryChars.TryGetValue(name, out string? chr))
            {
                ParseNary(sb, chr);
                return;
            }

            // Spacing commands spelled out as words.
            if (SpacingCommands.Contains(name))
            {
                EmitRun(sb, " ");
                return;
            }

            // \left / \right delimiters: emit the following delimiter char (best-effort, no auto-sizing).
            if (name == "left" || name == "right")
            {
                if (!End)
                {
                    char d = Peek;
                    _pos++;
                    if (d != '.') // "\left." / "\right." means an invisible delimiter.
                    {
                        EmitRun(sb, Xml(d.ToString()));
                    }
                }
                return;
            }

            // Text-ish wrappers: render the argument as ordinary content.
            if (name == "text" || name == "mathrm" || name == "operatorname" || name == "mathbf" ||
                name == "mathit" || name == "mathsf" || name == "mathcal" || name == "mathbb" ||
                name == "boldsymbol" || name == "bm")
            {
                string arg = ReadArgument();
                EmitWithPossibleScripts(sb, arg, baseIsRawOmml: true);
                return;
            }

            // Accents and similar single-argument decorations: emit the base (decoration dropped, best-effort).
            if (name == "hat" || name == "bar" || name == "vec" || name == "tilde" || name == "dot" ||
                name == "ddot" || name == "overline" || name == "underline" || name == "widehat" ||
                name == "widetilde")
            {
                string arg = ReadArgument();
                EmitWithPossibleScripts(sb, arg, baseIsRawOmml: true);
                return;
            }

            // Known symbol -> Unicode text run, which may itself carry scripts (e.g. "\pi^2", "\alpha_i").
            if (Symbols.TryGetValue(name, out string? sym))
            {
                EmitWithPossibleScripts(sb, Xml(sym));
                return;
            }

            // Unknown command: emit its name as literal text (best-effort), allowing trailing scripts.
            EmitWithPossibleScripts(sb, Xml(name));
        }

        /// <summary>
        /// Parses an n-ary operator (already identified) with optional <c>_{lo}</c>/<c>^{hi}</c> limits and the
        /// following token/group as the operand, emitting an <c>&lt;m:nary&gt;</c>.
        /// </summary>
        private void ParseNary(StringBuilder sb, string chr)
        {
            string sub = string.Empty;
            string sup = string.Empty;

            // Limits may appear in either order: _lo^hi or ^hi_lo.
            for (int i = 0; i < 2; i++)
            {
                SkipPlainSpaces();
                if (End)
                {
                    break;
                }
                if (Peek == '_')
                {
                    _pos++;
                    sub = ReadArgument();
                }
                else if (Peek == '^')
                {
                    _pos++;
                    sup = ReadArgument();
                }
                else
                {
                    break;
                }
            }

            // The operand: the next single token/group (best-effort).
            SkipPlainSpaces();
            string operand = ReadArgument();

            // subHide/supHide control whether the (empty) limit placeholders render.
            string subHide = sub.Length == 0 ? "<m:subHide m:val=\"1\"/>" : string.Empty;
            string supHide = sup.Length == 0 ? "<m:supHide m:val=\"1\"/>" : string.Empty;

            sb.Append("<m:nary><m:naryPr><m:chr m:val=\"").Append(Xml(chr)).Append("\"/><m:limLoc m:val=\"undOvr\"/>")
              .Append(subHide).Append(supHide).Append("</m:naryPr>")
              .Append("<m:sub>").Append(sub).Append("</m:sub>")
              .Append("<m:sup>").Append(sup).Append("</m:sup>")
              .Append("<m:e>").Append(WrapOrEmpty(operand)).Append("</m:e></m:nary>");
        }

        /// <summary>
        /// Given a base (either ordinary text to be wrapped in a run, or already-formed OMML), consume any
        /// immediately following <c>^</c>/<c>_</c> scripts and emit the appropriate
        /// <c>sSup</c>/<c>sSub</c>/<c>sSubSup</c> element — or just the base run when there is no script.
        /// </summary>
        /// <param name="sb">Destination buffer the OMML is appended to.</param>
        /// <param name="baseContent">The base run this script attaches to.</param>
        /// <param name="baseIsRawOmml">When true, <paramref name="baseContent"/> is already OMML; otherwise it
        /// is XML-escaped text that should be wrapped in a run.</param>
        /// <param name="baseAlreadyConsumed">When true, the script operator is at the current position and the
        /// base is empty (a leading script).</param>
        private void EmitWithPossibleScripts(StringBuilder sb, string baseContent,
            bool baseIsRawOmml = false, bool baseAlreadyConsumed = false)
        {
            string baseOmml = baseIsRawOmml
                ? baseContent
                : (baseContent.Length == 0 ? string.Empty : $"<m:r><m:t>{baseContent}</m:t></m:r>");

            // For a bare group base passed as already-formed OMML it may be empty; that's fine.
            if (!baseAlreadyConsumed)
            {
                SkipPlainSpaces();
            }

            string? sub = null;
            string? sup = null;

            // Read up to one subscript and one superscript in any order.
            for (int i = 0; i < 2 && !End; i++)
            {
                char c = Peek;
                if (c == '_' && sub is null)
                {
                    _pos++;
                    sub = ReadArgument();
                }
                else if (c == '^' && sup is null)
                {
                    _pos++;
                    sup = ReadArgument();
                }
                else
                {
                    break;
                }
            }

            string baseElem = $"<m:e>{baseOmml}</m:e>";

            if (sub is not null && sup is not null)
            {
                // Combined sub+sup -> native m:sSubSup.
                sb.Append("<m:sSubSup>").Append(baseElem)
                  .Append("<m:sub>").Append(sub).Append("</m:sub>")
                  .Append("<m:sup>").Append(sup).Append("</m:sup></m:sSubSup>");
            }
            else if (sup is not null)
            {
                sb.Append("<m:sSup>").Append(baseElem)
                  .Append("<m:sup>").Append(sup).Append("</m:sup></m:sSup>");
            }
            else if (sub is not null)
            {
                sb.Append("<m:sSub>").Append(baseElem)
                  .Append("<m:sub>").Append(sub).Append("</m:sub></m:sSub>");
            }
            else
            {
                sb.Append(baseOmml);
            }
        }

        /// <summary>Reads a contiguous run of ASCII letters as a command name (assumes the first char is a letter).</summary>
        private string ReadCommandName()
        {
            int start = _pos;
            while (!End && char.IsLetter(Peek))
            {
                _pos++;
            }
            return _src.Substring(start, _pos - start);
        }

        /// <summary>
        /// If the next non-space character is <c>[</c>, reads a bracketed optional argument (e.g. the root
        /// degree in <c>\sqrt[n]{x}</c>) and returns its OMML; otherwise returns <c>null</c>.
        /// </summary>
        private string? TryReadOptionalArgument()
        {
            SkipPlainSpaces();
            if (End || Peek != '[')
            {
                return null;
            }

            _pos++; // consume '['
            var inner = new StringBuilder();
            while (!End && Peek != ']')
            {
                char c = Peek;
                if (c == '{')
                {
                    _pos++;
                    inner.Append(ReadGroupBody());
                }
                else if (c == '\\')
                {
                    ParseCommand(inner);
                }
                else
                {
                    EmitRun(inner, Xml(c.ToString()));
                    _pos++;
                }
            }
            if (!End && Peek == ']')
            {
                _pos++; // consume ']'
            }
            return inner.ToString();
        }

        /// <summary>Skips literal space characters in the source (LaTeX ignores most inter-token whitespace).</summary>
        private void SkipPlainSpaces()
        {
            while (!End && Peek == ' ')
            {
                _pos++;
            }
        }
    }

    /// <summary>Wraps escaped <paramref name="text"/> in a single OMML run/text element.</summary>
    private static void EmitRun(StringBuilder sb, string text)
    {
        sb.Append("<m:r><m:t xml:space=\"preserve\">").Append(text).Append("</m:t></m:r>");
    }

    /// <summary>
    /// Returns <paramref name="omml"/> unchanged, or an empty run when it is blank, so structural slots such as
    /// <c>&lt;m:num&gt;</c> always contain at least one element (Word tolerates empty slots, but this keeps the
    /// tree tidy and unambiguous).
    /// </summary>
    private static string WrapOrEmpty(string omml)
        => omml.Length == 0 ? "<m:r><m:t></m:t></m:r>" : omml;

    /// <summary>
    /// Escapes XML's three content-significant characters so arbitrary LaTeX text is safe inside element
    /// content. (Quotes/apostrophes are not significant in element content and are left as-is.)
    /// </summary>
    private static string Xml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}
