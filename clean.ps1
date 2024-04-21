# Delete all build files and restore dependencies from nuget servers
# ------------------------------------------------------------------

Remove-Item -Path "Argentini.Fdeploy\bin" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "Argentini.Fdeploy\obj" -Recurse -Force -ErrorAction SilentlyContinue

dotnet restore Argentini.Fdeploy\Argentini.Fdeploy.csproj
