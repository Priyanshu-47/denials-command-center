using System.Globalization;
using AQ.Denials.Core;
using AQ.Denials.Ingest.X12;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>Finds the data pack. DATA_DIR wins; the repo-relative default is the fallback.</summary>
public static class TestData
{
    public static string DataDir
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
                return Path.GetFullPath(configured);

            // Walk up from the test bin directory to the repo root.
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(dir, "AQSoft_Assignment_Data_Pack_1");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            throw new DirectoryNotFoundException(
                "Data pack not found. Unzip it and set DATA_DIR to its location " +
                $"(looked for AQSoft_Assignment_Data_Pack_1 upward from {AppContext.BaseDirectory}).");
        }
    }

    public static string ReadText(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { DataDir }.Concat(parts).ToArray()));

    public static byte[] ReadBytes(params string[] parts) =>
        File.ReadAllBytes(Path.Combine(new[] { DataDir }.Concat(parts).ToArray()));

    public static IReadOnlyList<string> RemitFiles =>
        Directory.GetFiles(Path.Combine(DataDir, "remits"), "*.835")
            .Select(Path.GetFileName).Cast<string>().Order().ToList();
}

public class X12ParserTests
{
    private static ParsedRemit Parse(string name)
    {
        var bytes = TestData.ReadBytes("remits", name);
        return Remit835Parser.Parse(name,
            System.Text.Encoding.UTF8.GetString(bytes), bytes);
    }

    [Fact]
    public void All_four_remit_files_parse_without_malformed_segments()
    {
        Assert.Equal(4, TestData.RemitFiles.Count);
        foreach (var file in TestData.RemitFiles)
        {
            var remit = Parse(file);
            var malformed = remit.Issues.Where(i => i.ReasonCode == ExceptionReason.MalformedSegment);
            Assert.True(!malformed.Any(),
                $"{file}: {string.Join("; ", malformed.Select(m => m.Detail))}");
        }
    }

    [Theory]
    [InlineData("era_2026Q1.835")]
    [InlineData("era_2026Q2.835")]
    [InlineData("era_2026Q3.835")]
    public void Bpr_equals_sum_of_paid_on_every_file(string file)
    {
        var remit = Parse(file);
        var sumOfPaid = remit.Observations.Sum(o => o.PaidAmount);
        Assert.Equal(remit.BprTotal, sumOfPaid);
    }

    [Theory]
    [InlineData("era_2026Q1.835")]
    [InlineData("era_2026Q2.835")]
    [InlineData("era_2026Q3.835")]
    public void SubmittedCharge_equals_Paid_plus_signed_adjustments_on_every_clp(string file)
    {
        var remit = Parse(file);
        var checkedCount = 0;

        foreach (var obs in remit.Observations)
        {
            // Claim-level invariant: CLP03 == CLP04 + Σ CAS (signed), claim-level plus every SVC.
            var cas = obs.ClaimAdjustments.Sum(a => a.Amount)
                    + obs.Services.Sum(s => s.Adjustments.Sum(a => a.Amount));

            Assert.Equal(obs.SubmittedCharge, obs.PaidAmount + cas);
            checkedCount++;
        }

        Assert.Equal(remit.Observations.Count, checkedCount);
        Assert.True(checkedCount > 0, "no observations parsed");
    }

    [Fact]
    public void Header_fields_are_scoped_to_the_transaction_not_the_file()
    {
        var remit = Parse("era_2026Q3.835");

        // The file's leading transaction values are kept as a representative header...
        Assert.Equal("SMP12", remit.PayerId);
        Assert.Equal("2026-08-26", remit.CheckDate);
        Assert.NotNull(remit.TraceNumber);
        Assert.NotEmpty(remit.TraceNumber!);

        // ...but this file holds 9 transaction sets with 9 different check dates across
        // 4 different payers. Treating the file as one payer on one date would be wrong.
        Assert.Equal(9, remit.Transactions.Count);
        Assert.Equal(9, remit.TransactionCount);
        Assert.Equal(4, remit.Payers.Count);
        Assert.True(remit.CheckDates.Count > 1,
            $"expected several check dates, got {remit.CheckDates.Count}");

        Assert.All(remit.Transactions, t =>
        {
            Assert.NotEmpty(t.CheckDate);
            Assert.Contains(t.PayerId, remit.Payers);
        });

        // Every observation is anchored on a date the file actually declares.
        Assert.NotEmpty(remit.Observations);
        Assert.All(remit.Observations, o =>
        {
            Assert.Contains(o.CheckDate, remit.CheckDates);
            Assert.Contains(o.PayerId, remit.Payers);
        });
    }

    [Fact]
    public void Each_transaction_pays_exactly_its_own_BPR()
    {
        // Phase 0's invariant, at the unit it actually holds: BPR == Σ CLP04 per transaction
        // (i.e. per payer per check date), and the file total is the sum of those.
        foreach (var file in TestData.RemitFiles)
        {
            var remit = Parse(file);
            Assert.NotEmpty(remit.Transactions);

            foreach (var txn in remit.Transactions)
                Assert.True(txn.BprTotal == txn.PaidSum,
                    $"{file} txn {txn.TransactionIndex} ({txn.PayerId} {txn.CheckDate}): "
                  + $"BPR {txn.BprTotal} != paid {txn.PaidSum}");

            Assert.Equal(remit.Transactions.Sum(t => t.BprTotal), remit.BprTotal);
        }
    }

    [Fact]
    public void A_file_is_not_one_payer()
    {
        var q1 = Parse("era_2026Q1.835");
        Assert.Equal(4, q1.Payers.Count);
        Assert.Contains("NS401", q1.Payers);
        Assert.Contains("MRD55", q1.Payers);
    }

    [Fact]
    public void Envelope_counts_are_clean_in_this_pack()
    {
        foreach (var file in TestData.RemitFiles)
        {
            var remit = Parse(file);
            Assert.DoesNotContain(remit.Issues, i => i.ReasonCode == ExceptionReason.EnvelopeDefect);
        }
    }

    [Fact]
    public void Cas_pairs_are_walked_to_the_end_not_truncated_at_the_first_pair()
    {
        // CAS*CO*45*53.20*97*180.00 has two pairs; a regex that stops after the first would
        // under-count adjustments and break the CLP03 invariant.
        var content = "CLP*000001*1*233.20*0.00**7*CTRL*11*1~"
                    + "CAS*CO*45*53.20*97*180.00~";
        var parsed = Remit835Parser.Parse("synthetic.835", content);

        var obs = Assert.Single(parsed.Observations);
        Assert.Equal(2, obs.ClaimAdjustments.Count);

        // In THIS pack a normal write-off is positive: CLP03 = CLP04 + ΣCAS, i.e.
        // 233.20 = 0.00 + (53.20 + 180.00). Sign is preserved exactly as written — never
        // flipped, never absolute-valued (a reversal in this data does come through negative).
        Assert.Equal(53.20m, obs.ClaimAdjustments[0].Amount);
        Assert.Equal(180.00m, obs.ClaimAdjustments[1].Amount);
        Assert.Equal(233.20m, obs.ClaimAdjustments.Sum(a => a.Amount));
        Assert.Equal(233.20m, obs.PaidAmount + obs.ClaimAdjustments.Sum(a => a.Amount));
        Assert.Equal(233.20m, obs.SubmittedCharge);
    }

    [Fact]
    public void A_malformed_segment_becomes_an_issue_rather_than_an_exception()
    {
        var content = "BPR*X*not-a-number*C*ACH~"
                    + "CLP*000001*1*10.00*5.00**7*CTRL*11*1~"
                    + "CAS*CO**5.00~";

        var parsed = Remit835Parser.Parse("broken.835", content);

        Assert.Contains(parsed.Issues, i => i.ReasonCode == ExceptionReason.MalformedSegment);
        // The run still produced a usable observation.
        var obs = Assert.Single(parsed.Observations);
        Assert.Equal("000001", obs.RawClaimReference);
        Assert.Equal(10.00m, obs.SubmittedCharge);
    }

    [Fact]
    public void Payload_hash_ignores_the_envelope_so_a_rewrap_is_the_same_payment()
    {
        var a = "ISA*00*x~GS*HP~ST*835*0001~CLP*1~SE*1*0001~GE*1~IEA*1~";
        var b = "ISA*00*y~GS*ZZ~ST*835*0009~CLP*1~SE*1*0009~GE*1~IEA*9~";

        var pa = Remit835Parser.Parse("a.835", a);
        var pb = Remit835Parser.Parse("b.835", b);

        Assert.NotEqual(pa.FileSha256, pb.FileSha256);
        Assert.Equal(pa.PayloadSha256, pb.PayloadSha256);
    }

    [Fact]
    public void The_real_resent_file_duplicates_the_original_payload_but_not_the_file_bytes()
    {
        var original = Parse("era_2026Q2.835");
        var resent = Parse("era_2026Q2_resent_0719.835");

        Assert.NotEqual(original.FileSha256, resent.FileSha256);
        Assert.Equal(original.PayloadSha256, resent.PayloadSha256);
    }

    [Fact]
    public void Component_separator_comes_from_ISA16_not_a_hard_coded_position()
    {
        // ISA16 is ":" here and SVC01 is "HC:99232"; reading ISA15 ("P") would leave the
        // procedure code unparsed.
        var remit = Parse("era_2026Q3.835");
        var svc = remit.Observations.SelectMany(o => o.Services).First();
        Assert.Matches(@"^\d{4,5}$", svc.ProcedureCode);
    }

    [Fact]
    public void Money_never_uses_floating_point()
    {
        Assert.Equal(86.80m, Money.ParseX12("86.80"));
        Assert.Equal(-240.00m, Money.ParseX12("-240.00"));
        Assert.Equal(-240.00m, Money.ParseLoose("(240.00)"));
        Assert.Equal(1460.10m, Money.ParseLoose("$1,460.10"));
        Assert.Equal("-240.00", Money.ToX12(-240.00m));
        // The decimal type is exact for these values; the runtime type is never double.
        Assert.Equal(typeof(decimal), Money.ParseX12("0.10").GetType());
    }
}
