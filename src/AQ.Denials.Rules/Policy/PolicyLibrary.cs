namespace AQ.Denials.Rules.Policy;

/// <summary>Why a citation was or was not accepted. Every non-<see cref="Valid"/> value is a reason a draft gets rewritten or dropped.</summary>
public enum CitationVerdict
{
    /// <summary>The draft carried no citation at all — correct when this payer's policies say nothing about the denial.</summary>
    NoCitation,

    /// <summary>Cited clause exists in a document this payer may be cited from, and any quotation is real.</summary>
    Valid,

    /// <summary>No such file in the policy set — a hallucinated document name.</summary>
    UnknownFile,

    /// <summary>
    /// <b>Q3 breach.</b> The file belongs to a different payer. A denial may only cite the denying
    /// payer's own document, plus files explicitly written for all payers.
    /// </summary>
    WrongPayer,

    /// <summary>
    /// Right payer, but this document never names any CARC/RARC on the denial — so the draft has
    /// invented a connection between the two.
    /// </summary>
    NotRelevant,

    /// <summary>The cited clause number does not exist in that document.</summary>
    SectionNotFound,

    /// <summary>The draft claims to quote the clause, but those words are not in it.</summary>
    QuoteNotInSection,
}

/// <summary>
/// Holds every policy document and decides which of them a given denial may be argued from.
/// </summary>
/// <remarks>
/// <para>
/// Two independent gates, both of which a citation must pass:
/// </para>
/// <list type="number">
/// <item><description><b>Whose document is it?</b> The denying payer's own file, or a file named for all payers
/// (<c>ALL_PAYERS_*</c>). This is <b>Q3</b>, enforced structurally rather than asked of the model —
/// the model is never offered a document it is not allowed to use.</description></item>
/// <item><description><b>Does it talk about this denial?</b> The document must contain at least one clause that names a
/// CARC/RARC actually present on the denial, matched as <c>CARC 45</c> and never as a bare
/// substring — <c>within 60 days</c> and <c>CPT 99231-99233</c> both contain digits that would
/// otherwise match.</description></item>
/// </list>
/// <para>
/// A denial that passes neither gate has <b>no policy basis</b>, and the draft must say so rather
/// than borrow a neighbouring payer's document. The pack produces exactly that case, which is why
/// the rule is not theoretical.
/// </para>
/// </remarks>
public sealed class PolicyLibrary
{
    private const string AllPayersPrefix = "ALL_PAYERS";

    private readonly IReadOnlyDictionary<string, PolicyDocument> _documents;
    private readonly IReadOnlyDictionary<string, string> _payerOwnFile;

    public PolicyLibrary(
        IReadOnlyDictionary<string, PolicyDocument> documents,
        IReadOnlyDictionary<string, string> payerOwnFile)
    {
        _documents = documents ?? throw new ArgumentNullException(nameof(documents));
        _payerOwnFile = payerOwnFile ?? throw new ArgumentNullException(nameof(payerOwnFile));
    }

    // IReadOnlyDictionary<TKey,TValue>.Keys is declared as IEnumerable<TKey>, not as a
    // collection — take a snapshot rather than widening the property's contract.
    public IReadOnlyCollection<string> FileNames => _documents.Keys.ToArray();

    public PolicyDocument? DocumentFor(string fileName) =>
        _documents.TryGetValue(fileName, out var d) ? d : null;

    /// <summary>The file that belongs to this payer by itself, if any.</summary>
    public string? OwnFileFor(string payerId) =>
        _payerOwnFile.TryGetValue(payerId, out var f) ? f : null;

    /// <summary>
    /// Every document this denial may be argued from, in stable order. This list is what the
    /// prompt offers — the model picks a clause from here, never a file from its own memory.
    /// </summary>
    public IReadOnlyList<string> CitableFiles(string payerId, IEnumerable<string> carcs)
    {
        var codes = Normalize(carcs);
        var own = OwnFileFor(payerId);

        return _documents.Values
            .Where(d => IsPermittedFor(d.FileName, own))
            .Where(d => d.SectionsMentioning(codes).Count > 0)
            .Select(d => d.FileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The clauses inside the citable files that actually name this denial's codes.</summary>
    public IReadOnlyList<(string FileName, PolicySection Section)> CitableSections(
        string payerId, IEnumerable<string> carcs)
    {
        var codes = Normalize(carcs);
        var result = new List<(string, PolicySection)>();

        foreach (var fileName in CitableFiles(payerId, carcs))
        {
            var document = _documents[fileName];
            foreach (var section in document.SectionsMentioning(codes))
                result.Add((fileName, section));
        }

        return result;
    }

    /// <summary>
    /// Re-checks a citation the model produced. Called on the model's output, never on anything
    /// this system built itself — the whole point is that the second check does not trust the
    /// first.
    /// </summary>
    public CitationVerdict Validate(PolicyCitation? citation, string payerId, IEnumerable<string> carcs)
    {
        if (citation is null) return CitationVerdict.NoCitation;
        if (string.IsNullOrWhiteSpace(citation.FileName)) return CitationVerdict.UnknownFile;

        var document = _documents.TryGetValue(citation.FileName, out var d)
            ? d
            : null;
        if (document is null) return CitationVerdict.UnknownFile;

        // Q3 first: citing another payer's policy is the failure this whole class exists for,
        // so it must not be masked by a lesser problem found later.
        if (!IsPermittedFor(citation.FileName, OwnFileFor(payerId)))
            return CitationVerdict.WrongPayer;

        var codes = Normalize(carcs);
        if (document.SectionsMentioning(codes).Count == 0)
            return CitationVerdict.NotRelevant;

        var section = document.Sections.FirstOrDefault(
            s => s.Id.Equals(citation.SectionId, StringComparison.Ordinal));
        if (section is null) return CitationVerdict.SectionNotFound;

        if (!string.IsNullOrWhiteSpace(citation.Quote) && !QuoteAppears(section.Text, citation.Quote!))
            return CitationVerdict.QuoteNotInSection;

        return CitationVerdict.Valid;
    }

    private static bool IsPermittedFor(string fileName, string? ownFile) =>
        (ownFile is not null && fileName.Equals(ownFile, StringComparison.OrdinalIgnoreCase))
        || Path.GetFileName(fileName).StartsWith(AllPayersPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool QuoteAppears(string sectionText, string quote) =>
        Collapse(sectionText).Contains(Collapse(quote), StringComparison.OrdinalIgnoreCase);

    private static string Collapse(string s) =>
        string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static HashSet<string> Normalize(IEnumerable<string> carcs) =>
        carcs.Select(c => c.Trim())
             .Where(c => c.Length > 0)
             .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
