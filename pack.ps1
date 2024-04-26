if (Test-Path ".\Argentini.Fdeploy\nupkg") { Remove-Item ".\Argentini.Fdeploy\nupkg" -Recurse -Force }
. ./clean.ps1
Set-Location Argentini.Fdeploy
dotnet pack --configuration Release
Set-Location ..
