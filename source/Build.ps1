param(
    [Parameter(Mandatory = $true)]
    [string] $GameDirectory
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'A .NET 6 SDK (or a newer compatible SDK) is required.'
}

$required = @(
    'BepInEx\core\BepInEx.Unity.IL2CPP.dll',
    'BepInEx\interop\Assembly-CSharp.dll'
)
foreach ($relative in $required) {
    if (-not (Test-Path (Join-Path $GameDirectory $relative))) {
        throw "Missing reference: $relative. Use -GameDirectory pointing to your Zero Parades installation."
    }
}

$project = Join-Path $PSScriptRoot 'ZeroParadesInventoryFilter.csproj'
dotnet build $project -c Release "-p:ZPGameDir=$GameDirectory" --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Check the output above.' }

$dll = Get-ChildItem -Path (Join-Path $PSScriptRoot 'bin') -Filter 'ZeroParadesInventoryFilter.dll' -Recurse -File |
    Where-Object { $_.FullName -match '(\\|/)Release(\\|/)' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($null -eq $dll) { throw 'Build returned success, but the Release DLL could not be located.' }
Write-Host "Release DLL: $($dll.FullName)"
Write-Host 'Install only this DLL; remove older Inventory Filter and Clothing Filter builds.'
