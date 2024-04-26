rm -r Argentini.Fdeploy/nupkg
source clean.sh
cd Argentini.Fdeploy
dotnet pack --configuration Release
cd ..
