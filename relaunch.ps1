$ErrorActionPreference = 'SilentlyContinue'
$exe = 'C:\Users\Delz\source\repos\ClaudeVocal\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\ClaudeVocal.exe'
$proj = 'C:\Users\Delz\source\repos\ClaudeVocal\ClaudeVocal.csproj'

# 1) Tuer l'instance en cours (libère le verrou sur l'exe)
Get-Process ClaudeVocal -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

# 2) Rebuild x64 maintenant que l'exe est libre
& dotnet build $proj -nologo -p:Platform=x64 | Out-File 'C:\Users\Delz\source\repos\ClaudeVocal\relaunch.log' -Encoding utf8

# 3) Relancer dans une nouvelle fenêtre
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
