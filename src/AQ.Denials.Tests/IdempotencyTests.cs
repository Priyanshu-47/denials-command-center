using AQ.Denials.Ingest;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The two guarantees that make re-running an ingest safe: same input in, same state out, and the
/// order the files arrive in changes nothing.
/// </summary>
/// <remarks>
/// These are the tests that would catch the ordinary way these systems break — a
/// <c>Dictionary</c> enumerated in insertion order, a "keep the first one I saw" that depends on
/// directory enumeration, a sequence number assigned in arrival order. Each of those produces a
/// plausible-looking book that quietly differs from yesterday's, which is exactly the failure an
/// operator cannot detect by looking at a screen.
/// </remarks>
public class IdempotencyTests
{
    private static IngestInput Pack() => DataPack.Read(TestData.DataDir);

    [Fact]
    public void Ingesting_the_same_pack_twice_produces_the_same_state()
    {
        var first = IngestPipeline.Run(Pack());
        var second = IngestPipeline.Run(Pack());

        Assert.Equal(first.Checksum, second.Checksum);
        Assert.Equal(first.RowsIn, second.RowsIn);
        Assert.Equal(first.RowsMatched, second.RowsMatched);
        Assert.Equal(first.RowsExcepted, second.RowsExcepted);
        Assert.Equal(
            first.Reconciliation.CashExcludingDuplicate,
            second.Reconciliation.CashExcludingDuplicate);
    }

    [Fact]
    public void The_checksum_detects_a_real_change_in_state()
    {
        // Without this, the two tests above could pass because the checksum was accidentally
        // constant — a hash over nothing looks exactly like a hash over something stable.
        var before = IngestPipeline.Run(Pack()).Checksum;

        var changed = Pack();
        var modified = new IngestInput(
            changed.ClaimsCsv + "\nGPP-2026-009999,Ada,Lovelace,1990-01-01,M123,Test Payer,NS401,"
          + "2026-05-01,2026-05-10,1234567890,Dr Test,Test Hospital,21,1,99213,,1,10.00,J44.9,,,,,C07,Y",
            changed.WorklogXlsxPath,
            changed.Remits,
            changed.PayerRulesCsv,
            changed.CarcRarcCsv,
            changed.AdjustmentGroupsCsv,
            changed.PolicyDirectory);

        var after = IngestPipeline.Run(modified).Checksum;
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void Ingesting_the_files_in_a_different_order_produces_the_same_state()
    {
        var baseline = IngestPipeline.Run(Pack()).Checksum;

        // Distinct, deterministic permutations, so a coincidence is unlikely: reversed,
        // rotated, and one that puts the duplicate first — the case most likely to change
        // which file is treated as "the original".
        var orders = new (string Name, Func<List<RemitSource>, List<RemitSource>> Permute)[]
        {
            ("reversed", list => Enumerable.Reverse(list).ToList()),
            ("rotated", list => Enumerable.Range(0, list.Count).Select(i => list[(i + 3) % list.Count]).ToList()),
            ("duplicate first", list => list.OrderByDescending(r => r.FileName, StringComparer.Ordinal).ToList()),
        };

        foreach (var (name, permute) in orders)
        {
            var pack = Pack();
            var shuffled = new IngestInput(
                pack.ClaimsCsv,
                pack.WorklogXlsxPath,
                permute(pack.Remits.ToList()),
                pack.PayerRulesCsv,
                pack.CarcRarcCsv,
                pack.AdjustmentGroupsCsv,
                pack.PolicyDirectory);

            Assert.True(baseline == IngestPipeline.Run(shuffled).Checksum,
                $"permutation '{name}' changed the resulting state.");
        }
    }

    [Fact]
    public void Which_file_counts_as_the_original_is_decided_by_name_not_by_arrival()
    {
        // Both files share a payload; regardless of which one is handed over first, the
        // lexicographically earlier name must win, so an operator's answer never changes.
        var pack = Pack();
        var resentFirst = new IngestInput(
            pack.ClaimsCsv, pack.WorklogXlsxPath,
            pack.Remits.OrderByDescending(r => r.FileName, StringComparer.Ordinal).ToList(),
            pack.PayerRulesCsv, pack.CarcRarcCsv, pack.AdjustmentGroupsCsv, pack.PolicyDirectory);

        var outcome = IngestPipeline.Run(resentFirst);
        var duplicate = Assert.Single(outcome.State.RemitFiles, f => f.IsDuplicate);

        Assert.Equal("era_2026Q2.835", duplicate.DuplicateOfFileName);
        Assert.Equal("era_2026Q2_resent_0719.835", duplicate.FileName);
        Assert.Equal(118220.36m, outcome.Reconciliation.CashExcludingDuplicate);
    }

    [Fact]
    public void A_second_run_raises_the_same_exceptions()
    {
        var first = IngestPipeline.Run(Pack());
        var second = IngestPipeline.Run(Pack());

        static IEnumerable<string> Shape(IngestOutcome o) => o.Exceptions
            .Select(e => $"{e.ReasonCode}|{e.SourceFile}|{e.ClaimId}|{e.OriginalRef}")
            .OrderBy(s => s, StringComparer.Ordinal);

        Assert.Equal(Shape(first), Shape(second));
    }
}
