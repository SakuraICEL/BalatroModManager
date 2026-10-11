$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$sourceNames = @('Deps.cs','GitHub.cs','Mods.cs','Program.cs','Redesign.cs','Thunderstore.cs','Tools.cs','Util.cs')
$sourceFiles = @($sourceNames | ForEach-Object { Join-Path 'src' $_ })
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /debug- /out:BalatroModManager.exe /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
New-Item -ItemType Directory -Path outputs -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-PublicArchive($name, $files) {
    $target = Join-Path 'outputs' $name
    $stream = [IO.File]::Open($target, [IO.FileMode]::Create)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release file: $file" }
            $bytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot $file))
            $texts = @([Text.Encoding]::UTF8.GetString($bytes), [Text.Encoding]::Unicode.GetString($bytes))
            foreach ($text in $texts) {
                if (($env:USERNAME.Length -ge 4 -and $text.IndexOf($env:USERNAME, [StringComparison]::OrdinalIgnoreCase) -ge 0) -or $text -match '(?i)[A-Z]:\\Users\\[^\\\s"<>]+|/Users/[A-Za-z0-9_.-]+/|/home/[A-Za-z0-9_.-]+/|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[A-Z0-9]{16}') {
                    throw "Possible personal data or credential in release file: $file"
                }
            }
            $entry = $archive.CreateEntry($file.Replace('\','/'), [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2020,1,1,0,0,0,[TimeSpan]::Zero)
            $entryStream = $entry.Open()
            try { $entryStream.Write($bytes,0,$bytes.Length) } finally { $entryStream.Dispose() }
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
}
Write-PublicArchive 'BalatroModManager-Windows.zip' @('BalatroModManager.exe','README.md')
Write-PublicArchive 'BalatroModManager-Source.zip' ($sourceFiles + @('tests\PortableTests.cs','build.bat','build-public.ps1','.gitignore','README.md'))
Get-FileHash -Path outputs\BalatroModManager-Windows.zip,outputs\BalatroModManager-Source.zip -Algorithm SHA256 | ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) } | Set-Content -LiteralPath outputs\SHA256SUMS.txt -Encoding ascii
Write-Output 'Built allowlisted release archives; personal path and credential patterns checked.'
