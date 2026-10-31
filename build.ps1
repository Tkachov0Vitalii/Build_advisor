param([string]$ManagedDir = "")
$arguments = @("build", "BuildAdvisor.csproj", "-c", "Release")
if ($ManagedDir) { $arguments += "-p:Sts2ManagedDir=$ManagedDir" }
dotnet @arguments
exit $LASTEXITCODE

