using AQ.Denials.Ingest;
using AQ.Denials.Ingest.Readers;
using Xunit;

namespace AQ.Denials.Tests;

/// <summary>
/// The association between a payer and its policy document is <b>derived</b> — the pack has no
/// policy column, so the mapping is inferred from the file titles' payer prefix. An inferred
/// mapping that nobody checks is exactly how a denial ends up citing another payer's policy, so
/// it is pinned here rather than left to be discovered in Phase 2.
/// </summary>
public class PolicyReferenceTests
{
    private static readonly string Policies = Path.Combine(TestData.DataDir, "payer_policies");

    [Fact]
    public void Every_payer_in_the_pack_maps_to_a_policy_file_that_actually_exists()
    {
        var mapping = ReferenceDataReader.PolicyFileForPayer(Policies);

        // The four payers named in README.txt. An unknown payer must fail loudly rather than
        // silently receive no policy, because "no policy" reads as "no basis to appeal".
        Assert.Equal(
            new[] { "CSA77", "MRD55", "NS401", "SMP12" },
            mapping.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        foreach (var (payerId, fileName) in mapping)
        {
            var path = Path.Combine(Policies, fileName);
            Assert.True(File.Exists(path), $"{payerId} maps to {fileName}, which does not exist");

            var text = File.ReadAllText(path);
            Assert.True(text.Trim().Length > 0, $"{fileName} is empty");
        }
    }

    [Fact]
    public void The_all_payers_document_is_never_returned_as_a_payers_own_citation()
    {
        // ALL_PAYERS_MOD25-2026.md is titled for all four payers, so it is tempting to hand it
        // to everyone as their own document. Q3 allows an ALL_PAYERS_* file to be *cited* by any
        // payer, but it is nobody's *own* document — so it must not appear in this map, and a
        // denial whose payer has no same-payer section has to say so rather than be handed this
        // file as its primary policy.
        var mapping = ReferenceDataReader.PolicyFileForPayer(Policies);

        Assert.DoesNotContain(mapping.Values, v =>
            v.Equals("ALL_PAYERS_MOD25-2026.md", StringComparison.OrdinalIgnoreCase));

        // MPPO_DX-EXCL-03 is the counter-case: its preamble claims to cover every payer, but the
        // document belongs to Meridian PPO — so exactly one payer may cite it and no other.
        Assert.Equal("MPPO_DX-EXCL-03.md", mapping["MRD55"]);
        Assert.Single(mapping, kv => kv.Value == "MPPO_DX-EXCL-03.md");
    }

    [Fact]
    public void All_five_policy_files_parse_to_addressable_clauses()
    {
        var policies = ReferenceDataReader.ReadPolicies(Policies);

        Assert.Equal(5, policies.Count);
        Assert.Contains("ALL_PAYERS_MOD25-2026.md", policies.Keys);

        foreach (var (name, document) in policies)
        {
            // Byte length only proved the file was non-empty. What the system actually needs is
            // the ability to point at a specific clause — a policy that parses to nothing is
            // unusable for citation and must fail here, not produce a draft citing a clause that
            // cannot be found.
            Assert.True(document.Sections.Count >= 1, $"{name} parsed to no clauses");
            Assert.NotEmpty(document.Title);

            foreach (var section in document.Sections)
            {
                Assert.False(string.IsNullOrWhiteSpace(section.Text),
                    $"{name} clause {section.Id} is empty");
            }

            // Clause ids must be unique or "section 3" is ambiguous — the citation could not be
            // resolved to one place, which is the whole thing a citation is for.
            Assert.Equal(
                document.Sections.Count,
                document.Sections.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void The_rules_table_and_the_policy_files_cover_the_same_payers()
    {
        var rules = ReferenceDataReader.ReadPayerRules(TestData.ReadText("payer_rules.csv"));
        var mapping = ReferenceDataReader.PolicyFileForPayer(Policies);

        // A payer with a window but no policy can be dated but not argued with; a payer with a
        // policy but no window can be argued with but not dated. Neither is survivable.
        Assert.Equal(
            rules.Select(r => r.PayerId).OrderBy(p => p, StringComparer.Ordinal),
            mapping.Keys.OrderBy(p => p, StringComparer.Ordinal));
    }
}
