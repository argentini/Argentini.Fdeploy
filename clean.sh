# Delete all build files and restore dependencies from nuget servers
# ------------------------------------------------------------------

rm -r Argentini.Fdeploy/bin
rm -r Argentini.Fdeploy/obj

dotnet restore Argentini.Fdeploy/Argentini.Fdeploy.csproj
