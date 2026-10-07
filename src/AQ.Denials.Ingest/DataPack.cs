using AQ.Denials.Core;

namespace AQ.Denials.Ingest;

/// <summary>A remittance file's contents, read but not yet parsed.</summary>
public sealed record RemitSource(string FileName, string Content, byte[] RawBytes);

/// <summary>Everything the pipeline needs, resolved and read once.</summary>
public sealed record IngestInput(
    string ClaimsCsv,
    string WorklogXlsxPath,
    IReadOnlyList<RemitSource> Remits,
    string PayerRulesCsv,
    string CarcRarcCsv,
    string AdjustmentGroupsCsv,
    string PolicyDirectory);

/// <summary>
/// Resolves the data pack from <c>DATA_DIR</c> and fails fast, listing <b>every</b> missing file
/// at once rather than one per run.
/// </summary>
/// <remarks>
/// <b>Nothing here moves, renames or deletes the originals.</b> The pack is read in place and
/// mounted read-only in Docker; <c>DATA_DIR</c> only says where it is. The required list covers
/// the fixed reference inputs, while <c>remits/</c> and <c>payer_policies/</c> are validated as
/// directories with at least one usable file — so a later round can drop in a new remit or a new
/// policy without anyone editing this file, but a missing <c>claims_export.csv</c> still stops
/// the run before anything is written.
/// </remarks>
public static class DataPack
{
    public const string DefaultDataDir = "./AQSoft_Assignment_Data_Pack_1";

    public static readonly string[] RequiredFiles =
    [
        "README.txt",
        "claims_export.csv",
        "denials_worklog.xlsx",
        "labeled_denials_sample.csv",
        "payer_rules.csv",
        "carc_rarc_reference.csv",
        "claim_adjustment_group_codes.csv",
    ];

    public static readonly string[] RequiredDirectories = ["remits", "payer_policies"];

    /// <summary>Resolve and read the pack. Throws listing every problem found.</summary>
    /// <param name="dataDir">Defaults to <c>DATA_DIR</c>, then <see cref="DefaultDataDir"/>.</param>
    public static IngestInput Read(string? dataDir = null)
    {
        var root = string.IsNullOrWhiteSpace(dataDir)
            ? Environment.GetEnvironmentVariable("DATA_DIR")
            : dataDir;
        if (string.IsNullOrWhiteSpace(root)) root = DefaultDataDir;

        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                $"DATA_DIR '{root}' does not exist. Unzip the data pack and point DATA_DIR at it.");

        var problems = new List<string>();

        foreach (var relative in RequiredFiles)
            if (!File.Exists(Path.Combine(root, relative)))
                problems.Add($"missing file: {relative}");

        foreach (var relative in RequiredDirectories)
            if (!Directory.Exists(Path.Combine(root, relative)))
                problems.Add($"missing directory: {relative}/");

        // Directories that must contain something usable — checked only if they exist, so the
        // "missing directory" message above is not buried under a cascade.
        if (Directory.Exists(Path.Combine(root, "remits")) &&
            !Directory.EnumerateFiles(Path.Combine(root, "remits"), "*.835").Any())
            problems.Add("remits/ contains no .835 files");

        if (Directory.Exists(Path.Combine(root, "payer_policies")) &&
            !Directory.EnumerateFiles(Path.Combine(root, "payer_policies"), "*.md").Any())
            problems.Add("payer_policies/ contains no .md files");

        if (problems.Count > 0)
            throw new FileNotFoundException(
                $"Data pack at '{root}' is incomplete — {problems.Count} problem(s):\n  "
              + string.Join("\n  ", problems)
              + "\nUnzip the full pack, then set DATA_DIR to its root.");

        var remitsDir = Path.Combine(root, "remits");

        // Ordinal file-name order: which file counts as "the original" when two share a payload
        // must not depend on what the filesystem happened to return.
        var remits = Directory.EnumerateFiles(remitsDir, "*.835")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(f => new RemitSource(
                Path.GetFileName(f),
                File.ReadAllText(f),
                File.ReadAllBytes(f)))
            .ToList();

        return new IngestInput(
            ClaimsCsv: File.ReadAllText(Path.Combine(root, "claims_export.csv")),
            WorklogXlsxPath: Path.Combine(root, "denials_worklog.xlsx"),
            Remits: remits,
            PayerRulesCsv: File.ReadAllText(Path.Combine(root, "payer_rules.csv")),
            CarcRarcCsv: File.ReadAllText(Path.Combine(root, "carc_rarc_reference.csv")),
            AdjustmentGroupsCsv: File.ReadAllText(Path.Combine(root, "claim_adjustment_group_codes.csv")),
            PolicyDirectory: Path.Combine(root, "payer_policies"));
    }

    /// <summary>For the README's environment variable table and the API's <c>/health</c> output.</summary>
    public static IReadOnlyList<string> DescribeMissing(string dataDir) =>
        Directory.Exists(dataDir)
            ? RequiredFiles.Where(f => !File.Exists(Path.Combine(dataDir, f))).ToList()
            : RequiredFiles.ToList();
}
