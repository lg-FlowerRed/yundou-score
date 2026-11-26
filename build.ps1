$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $compiler = @('Framework64', 'Framework') | ForEach-Object {
        Join-Path $env:WINDIR "Microsoft.NET\$_\v4.0.30319\csc.exe"
    } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$compiler) { throw 'Windows .NET Framework C# compiler not found.' }
    & $compiler /nologo /utf8output /warn:4 /langversion:5 /optimize+ /platform:anycpu /target:exe /out:YundouScore-v2.exe /resource:Site.js,Site.js /resource:web/index.html,web/index.html /resource:web/app.css,web/app.css /resource:web/app.js,web/app.js /reference:System.Web.dll /reference:System.Web.Extensions.dll /reference:Microsoft.VisualBasic.dll /reference:System.IO.Compression.dll Program.cs Edge.cs Xlsx.cs ScoreCache.cs WebUi.cs ProvinceExport.cs CodeExport.cs AnswerArchive.cs
    exit $LASTEXITCODE
} finally { Pop-Location }
