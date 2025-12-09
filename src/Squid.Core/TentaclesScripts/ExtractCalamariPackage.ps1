# When we push up the latest Tentacle package, we need to extract it. Since it is a NuGet package we use `Tentacle.exe extract`

$ErrorActionPreference = "Stop"

Write-Host "##octopus[stdout-verbose]"

Write-Host "Checking for ${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}\Success.txt"
if ((Test-Path "${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}\Success.txt") -eq $true)
{
	Write-Host "{{CalamariPackage}} {{CalamariPackageVersion}} already extracted, doing nothing"
    Exit 0
}

if ((Test-Path "${env:TentacleExecutablePath}") -eq $false)
{
	Write-Error "Tentacle.exe not found: ${env:TentacleExecutablePath}"
    Exit 1
}

# If the Success.txt file does not exist, but the directory containing it does, then it must be a partially failed update. If so, we need to 
# purge it before we proceed.
if (((Test-Path "${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}\Success.txt") -eq $false) -and ((Test-Path "${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}") -eq $true))
{
    Get-ChildItem -Path "${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}" -Include *.* -Recurse | foreach { $_.Delete() } | Out-Null
}


& "${env:TentacleExecutablePath}" extract --package "{{CalamariPackage}}.{{CalamariPackageVersion}}.nupkg" --destination "${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}" --console
if ($LastExitCode -ne 0) 
{
    Write-Error "Extraction of {{CalamariPackage}}.{{CalamariPackageVersion}}.nupkg failed"
	Exit 1
}

if("{{SupportPackage}}" -ne "")
{
	& "${env:TentacleExecutablePath}" extract --package "{{SupportPackage}}.{{SupportPackageVersion}}.nupkg" --destination "${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}" --console
	if ($LastExitCode -ne 0) 
	{
		Write-Error "Extraction of {{SupportPackage}}.{{SupportPackageVersion}}.nupkg failed"
		Exit 1
	}

}

New-Item "${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}\Success.txt" -Type File | Out-Null
Write-Host "{{CalamariPackage}} {{CalamariPackageVersion}} extracted to ${env:TentacleHome}\Calamari\{{CalamariPath}}{{CalamariPackageVersion}}"

Write-Host "Cleaning up old {{CalamariPackage}} versions..."

function Delete-CalamariFolders($calamariFoldersToDelete) {
    if($calamariFoldersToDelete -and $calamariFoldersToDelete.Count -gt 0) {
	    $calamariFoldersToDelete | % {
            Write-Host "Removing $($_.FullName)..."
		    Remove-Item $_.FullName -Recurse -Force
	    }
    } else {
        Write-Host "Found no old versions of {{CalamariPackage}} to delete..."
    }
}

if([bool]::Parse("{{UsesCustomPackageDirectory}}")) {
	$calamariVersionsToKeep = ("{{CalamariPackageVersionsToKeep}}").Split("|")
	Write-Host "Custom builds of {{CalamariPackage}} used, keeping the last $($calamariVersionsToKeep.Count) versions..."
	$calamariVersionsToKeep | foreach { Write-Host "Keeping version $_." }
	Delete-CalamariFolders @(Get-ChildItem -Path "${env:TentacleHome}\Calamari\{{CalamariPath}}" | ? { -not ($calamariVersionsToKeep -contains $_.Name) } | Select-Object -Property FullName)
} else {
	Write-Host "Keeping only {{CalamariPackage}} version {{CalamariPackageVersion}}..."
    Delete-CalamariFolders @(Get-ChildItem -Path "${env:TentacleHome}\Calamari\{{CalamariPath}}" | ? { $_.Name -ne "{{CalamariPackageVersion}}" } | Select-Object -Property FullName)
}

Write-Host "##octopus[stdout-default]"

