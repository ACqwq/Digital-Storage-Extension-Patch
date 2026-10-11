param(
    [switch]$Clean
)
$ErrorActionPreference = 'Stop'

# Workspace root = three levels above this script's folder
#   (mods\DigitalStoragePatch\Source -> mods\DigitalStoragePatch -> mods -> workspace root)
$modDir = Split-Path $PSScriptRoot -Parent
$root   = Split-Path (Split-Path $modDir -Parent) -Parent
$srcDir = Join-Path $modDir 'Source'
$asmDir = Join-Path $modDir 'Assemblies'
$csc    = 'E:\360data\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'

$managed = 'D:\SteamLibrary\steamapps\common\RimWorld\RimWorldWin64_Data\Managed'
$harmony = 'D:\SteamLibrary\steamapps\workshop\content\294100\2009463077\Current\Assemblies\0Harmony.dll'
# Reference assembly = the TARGET mod we patch (Digital Storage 4.0).
$origDll = Join-Path $root 'analysis\in\DigitalStorage40.dll'

foreach ($p in @($csc, $managed, $harmony, $origDll)) {
    if (-not (Test-Path $p)) { throw "missing required input: $p" }
}

if ($Clean -and (Test-Path $asmDir)) { Remove-Item $asmDir -Recurse -Force }
New-Item -ItemType Directory -Path $asmDir -Force | Out-Null

# Unity engine modules we actually touch (Rect/Vector2/GUI/Text/Widgets are split across these)
$unityRefs = @(
    'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.TextRenderingModule.dll'
)

$refs = New-Object System.Collections.Generic.List[string]
$refs.Add((Join-Path $managed 'Assembly-CSharp.dll'))
$refs.Add($harmony)
$refs.Add($origDll)
foreach ($u in $unityRefs) {
    $p = Join-Path $managed $u
    if (-not (Test-Path $p)) { throw "missing Unity module: $p" }
    $refs.Add($p)
}

# Core framework: use the v4.7.2 reference pack (facades included),
# otherwise csc picks up mscorlib 4.0 and clashes with the game's assembly identities.
$tmp  = Join-Path $env:TEMP 'ilspy_pkg'
$refd = Join-Path $tmp 'net472ref\build\.NETFramework\v4.7.2'
$fac  = Join-Path $refd 'Facades'
$skipFx = 'System.EnterpriseServices.Wrapper.dll', 'System.EnterpriseServices.Thunk.dll'

$lines = New-Object System.Collections.Generic.List[string]
'-target:library', '-nologo', '-nostdlib', '-langversion:9', '-platform:anycpu',
'-optimize+', '-deterministic',
'-nowarn:1701,1702,8600,8601,8602,8603,8604,0436,0169,0649' |
    ForEach-Object { $lines.Add($_) }
$lines.Add('-out:"' + (Join-Path $asmDir 'DigitalStoragePatch.dll') + '"')

if (Test-Path $refd) {
    Get-ChildItem $refd -Filter '*.dll' | Where-Object { $skipFx -notcontains $_.Name } |
        ForEach-Object { $lines.Add('-r:"' + $_.FullName + '"') }
    Get-ChildItem $fac -Filter '*.dll' |
        ForEach-Object { $lines.Add('-r:"' + $_.FullName + '"') }
} else {
    throw "net472 reference pack not found at $refd (see 开发参考/01-环境与工具链.md)"
}

foreach ($r in $refs) { $lines.Add('-r:"' + $r + '"') }

Get-ChildItem $srcDir -Filter '*.cs' -Recurse |
    Where-Object { $_.FullName -notmatch '\\verify\\' } |
    Sort-Object FullName |
    ForEach-Object { $lines.Add('"' + $_.FullName + '"') }

$rsp = Join-Path $srcDir 'build.rsp'
# NOTE: csc reads response files as ANSI unless a BOM is present, and this workspace path
# contains non-ASCII characters. Write UTF-8 WITH BOM or the source paths get mangled.
# Also: keep the rsp OUT of Assemblies/ so the shipped mod folder stays clean.
[System.IO.File]::WriteAllLines($rsp, $lines, (New-Object System.Text.UTF8Encoding($true)))

& $csc "@$rsp"
$code = $LASTEXITCODE
Write-Host "csc exit: $code"
if ($code -ne 0) { throw "build failed" }

# 0Harmony.dll 不再随补丁发布（v0.33 起由玩家自行订阅 Harmony，见 tools/build.py 文件头）。
# $harmony 只作为编译引用使用。
$stray = Join-Path $asmDir '0Harmony.dll'
if (Test-Path $stray) { Remove-Item $stray -Force }
Write-Host "built:"
Get-ChildItem $asmDir -File | Select-Object Name, @{n='KB';e={[math]::Round($_.Length/1KB,1)}} | Format-Table -AutoSize
