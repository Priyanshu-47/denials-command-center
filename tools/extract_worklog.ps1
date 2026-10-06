# Converts denials_worklog.xlsx (inline strings only) -> worklog_extracted.csv
param([string]$Xlsx = 'E:\AQcode\AQSoft_Assignment_Data_Pack_1\denials_worklog.xlsx',
      [string]$Out  = 'E:\AQcode\AQSoft_Assignment_Data_Pack_1\worklog_extracted.csv')

Add-Type -AssemblyName System.IO.Compression.FileSystem
$tmp = Join-Path $env:TEMP ('wl_' + [guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($Xlsx, $tmp)
[xml]$xml = Get-Content (Join-Path $tmp 'xl\worksheets\sheet1.xml') -Raw
Remove-Item $tmp -Recurse -Force

$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace('m','http://schemas.openxmlformats.org/spreadsheetml/2006/main')

$rows = @()
foreach ($row in $xml.SelectNodes('//m:sheetData/m:row',$ns)) {
    $cells = @{}
    foreach ($c in $row.SelectNodes('m:c',$ns)) {
        $ref = $c.GetAttribute('r')
        $col = ($ref -replace '\d','')
        $t = $c.SelectSingleNode('m:is/m:t',$ns)
        $v = $c.SelectSingleNode('m:v',$ns)
        $val = if ($t) { $t.InnerText } elseif ($v) { $v.InnerText } else { '' }
        $cells[$col] = $val
    }
    $rows += ,(@($cells['A'],$cells['B'],$cells['C'],$cells['D'],$cells['E'],$cells['F'],$cells['G'],$cells['H']))
}

$sw = New-Object IO.StreamWriter($Out,$false,(New-Object Text.UTF8Encoding($false)))
foreach ($r in $rows) {
    $esc = $r | ForEach-Object { $s = if ($null -eq $_) { '' } else { [string]$_ }; '"' + $s.Replace('"','""') + '"' }
    $sw.WriteLine(($esc -join ','))
}
$sw.Close()
"Wrote $Out ($($rows.Count) rows incl header)"
