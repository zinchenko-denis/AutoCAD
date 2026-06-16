<#
  build.ps1 — одна команда: собирает плагин (DLL) и движок (dxf_spec.exe),
  складывает готовый бандл ATableSpec.bundle и упаковывает его в zip.

  Запускать на Windows, где установлены:
    - AutoCAD 2021 (или указать -AcadDir на свою установку);
    - .NET SDK (для сборки net48; нужен также таргет-пак .NET Framework 4.8);
    - Python 3.x в PATH (для заморозки движка через PyInstaller).

  Пример:
    powershell -ExecutionPolicy Bypass -File build.ps1
    powershell -ExecutionPolicy Bypass -File build.ps1 -AcadDir "C:\Program Files\Autodesk\AutoCAD 2021"
#>
param(
    [string]$AcadDir = "C:\Program Files\Autodesk\AutoCAD 2021"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Write-Host "== ATableSpec build ==  AcadDir=$AcadDir"

# --- 1. C#-плагин (net48) ---
Write-Host "`n[1/4] Сборка C# плагина..."
$csproj = Join-Path $root "src\AtSpecPlugin\AtSpecPlugin.csproj"
dotnet build $csproj -c Release -p:AcadDir="$AcadDir"
$dll = Join-Path $root "src\AtSpecPlugin\bin\Release\AtSpecPlugin.dll"
if (-not (Test-Path $dll)) { throw "DLL не собралась: $dll" }

# --- 2. Движок dxf_spec.exe (PyInstaller) ---
Write-Host "`n[2/4] Заморозка движка (PyInstaller)..."
python -m pip install --upgrade --quiet pyinstaller ezdxf pyyaml openpyxl
$pybuild = Join-Path $root ".pybuild"
pyinstaller --onefile --name dxf_spec `
    (Join-Path $root "engine\dxf_spec.py") `
    --distpath (Join-Path $pybuild "dist") `
    --workpath (Join-Path $pybuild "work") `
    --specpath $pybuild
$exe = Join-Path $pybuild "dist\dxf_spec.exe"
if (-not (Test-Path $exe)) { throw "Движок не собрался: $exe" }

# --- 3. Сборка бандла ---
Write-Host "`n[3/4] Сборка бандла..."
$dist = Join-Path $root "dist"
$bundle = Join-Path $dist "ATableSpec.bundle"
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force (Join-Path $bundle "Contents\net48")  | Out-Null
New-Item -ItemType Directory -Force (Join-Path $bundle "Contents\engine") | Out-Null
Copy-Item (Join-Path $root "bundle\ATableSpec.bundle\PackageContents.xml") $bundle
Copy-Item $dll  (Join-Path $bundle "Contents\net48\")
Copy-Item $exe  (Join-Path $bundle "Contents\engine\")
Copy-Item (Join-Path $root "engine\mapping.yaml") (Join-Path $bundle "Contents\engine\")

# --- 4. ZIP ---
Write-Host "`n[4/4] Упаковка ZIP..."
$zip = Join-Path $dist "ATableSpec.bundle.zip"
Compress-Archive -Path $bundle -DestinationPath $zip -Force

Write-Host "`nГОТОВО."
Write-Host "  Бандл: $bundle"
Write-Host "  ZIP  : $zip   <- это и есть файл для конструктора"
