using System.IO.Compression;
using System.Text;
using AQ.Denials.Ingest;

namespace AQ.Denials.Tests;

/// <summary>
/// Builds a small synthetic data pack in a temp directory, so the guards that the real pack
/// happens not to exercise (orphan remits, foreign claim ids, cross-file natural-key collisions,
/// unadjudicated claims) can be tested against inputs that do contain them.
/// </summary>
/// <remarks>
/// The real pack proves the happy path and duplicate-payload detection; it proves nothing about
/// what happens when a remit names a claim nobody exported. Testing that only against real data
/// would mean waiting for the defect to arrive in production.
/// </remarks>
public sealed class TestPack
{
    public string Root { get; }

    private readonly Dictionary<string, StringBuilder> _remits = new(StringComparer.Ordinal);
    private readonly StringBuilder _claims = new();
    private readonly StringBuilder _worklog = new();
    private int _worklogRow = 1;

    private TestPack(string root) => Root = root;

    public static TestPack Create(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "aq_test_pack_" + name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "remits"));
        Directory.CreateDirectory(Path.Combine(root, "payer_policies"));

        var pack = new TestPack(root);

        File.WriteAllText(Path.Combine(root, "README.txt"), "synthetic\n");
        File.WriteAllText(Path.Combine(root, "labeled_denials_sample.csv"), "claim_id,label\n");
        File.WriteAllText(Path.Combine(root, "payer_rules.csv"),
            "payer,payer_id,timely_filing_days_from_dos,appeal_window_days_from_denial,corrected_claim_window_days_from_denial\n"
          + "Northstar Health Plan,NS401,180,180,180\n");
        File.WriteAllText(Path.Combine(root, "carc_rarc_reference.csv"),
            "type,code,description\n"
          + "CARC,1,Deductible amount.\n"
          + "CARC,45,Charge exceeds fee schedule.\n"
          + "CARC,11,The diagnosis is inconsistent with the procedure.\n"
          + "RARC,M15,Bundled.\n");
        File.WriteAllText(Path.Combine(root, "claim_adjustment_group_codes.csv"),
            "group,meaning\nCO,\"Contractual obligation\"\nPR,Patient responsibility\n");
        File.WriteAllText(Path.Combine(root, "payer_policies", "NSHP_TEST.md"), "# test policy\n");

        pack._claims.AppendLine(
            "claim_id,patient_first,patient_last,patient_dob,member_id,payer,payer_id,dos,"
          + "submitted_date,rendering_npi,rendering_provider,facility,pos,line_no,cpt,modifier,"
          + "units,charge,dx1,dx2,dx3,dx4,auth_number,coder_id,prebill_reviewed");

        pack._worklog.AppendLine(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
          + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        pack._worklog.Append(Row(1, "Claim #", "Date Logged", "Patient", "Amt", "Notes", "Payer", "Owner", "Status"));

        return pack;
    }

    /// <summary>Add a claim line to the export.</summary>
    public TestPack Claim(string claimId, decimal charge, string payerId = "NS401",
        string dos = "2026-05-01", string submitted = "2026-05-10", string dx1 = "J44.9")
    {
        _claims.Append(claimId)
            .Append(",Ada,Lovelace,1990-01-01,M123,Test Payer,")
            .Append(payerId).Append(',')
            .Append(dos).Append(',')
            .Append(submitted)
            .Append(",1234567890,Dr Test,Test Hospital,21,1,99213,,1,")
            .Append(charge.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
            .Append(',')
            .Append(dx1).Append(",,,,,C07,Y\n");
        return this;
    }

    /// <summary>Add a worklog row. <paramref name="date"/> is written verbatim.</summary>
    public TestPack Worklog(string claimRef, string date, string amount = "100.00",
        string status = "Open", string owner = "alice")
    {
        _worklogRow++;
        _worklog.Append(Row(_worklogRow, claimRef, date, "Ada", amount, "note", "Test Payer", owner, status));
        return this;
    }

    /// <summary>Add a remittance file.</summary>
    public TestPack Remit(string fileName, string content)
    {
        _remits[fileName] = new StringBuilder(content);
        return this;
    }

    /// <summary>A minimal but structurally valid 835 carrying one claim per transaction set.</summary>
    public static string Simple835(
        string claimRef,
        decimal charge,
        decimal paid,
        string groupCode,
        string carc,
        decimal adjustment,
        string payerId = "NS401",
        string checkDate = "20260615",
        string trace = "CHECK001",
        string controlNumber = "CTRL1",
        string status = "4",
        int interchange = 1)
    {
        var isa13 = interchange.ToString("000000000", System.Globalization.CultureInfo.InvariantCulture);
        var adjustmentSegment = adjustment == 0m
            ? string.Empty
            : $"CAS*{groupCode}*{carc}*{adjustment.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}~";

        return $"ISA*00*          *00*          *ZZ*SENDER        *ZZ*RECEIVER      *260101*1200*^*00501*{isa13}*0*P*:~\n"
             + "GS*HP*SENDER*RECEIVER*20260101*1200*1*X*005010X221A1~\n"
             + "ST*835*0001~\n"
             + $"BPR*I*{paid.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}*C*ACH*CCP*01*111111111*DA*222222222*{trace}*1~\n"
             + $"TRN*1*{trace}*1{payerId}~\n"
             + $"DTM*405*{checkDate}~\n"
             + $"REF*2U*{payerId}~\n"
             + $"CLP*{claimRef}*{status}*"
               + charge.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "*"
               + paid.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
               + $"*0*12*{controlNumber}*{carc}*1~\n"
             + adjustmentSegment
             + "SE*8*0001~\n"
             + "GE*1*1~\n"
             + $"IEA*1*{isa13}~\n";
    }

    public IngestInput Build()
    {
        foreach (var (name, content) in _remits)
            File.WriteAllText(Path.Combine(Root, "remits", name), content.ToString());

        File.WriteAllText(Path.Combine(Root, "claims_export.csv"), _claims.ToString());

        _worklog.Append("</sheetData></worksheet>");
        var worklogPath = Path.Combine(Root, "denials_worklog.xlsx");
        if (File.Exists(worklogPath)) File.Delete(worklogPath);
        using (var zip = ZipFile.Open(worklogPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("xl/worksheets/sheet1.xml");
            using var s = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(_worklog.ToString());
            s.Write(bytes, 0, bytes.Length);
        }

        return DataPack.Read(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (IOException) { /* temp dir; the OS will collect it */ }
    }

    private static string Row(int number, params string[] cells)
    {
        var sb = new StringBuilder($"<row r=\"{number}\">");
        for (var i = 0; i < cells.Length; i++)
        {
            var reference = Column(i) + number;
            sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{System.Security.SecurityElement.Escape(cells[i])}</t></is></c>");
        }
        return sb.Append("</row>").ToString();
    }

    private static string Column(int index)
    {
        var name = string.Empty;
        do
        {
            name = (char)('A' + index % 26) + name;
            index = index / 26 - 1;
        } while (index >= 0);
        return name;
    }
}
