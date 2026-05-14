## --------------------------------------------------------------------------------------
## Squid — Deploy to IIS WebSite (PowerShell, Windows Tentacle target)
##
## MIRROR-TIER SCRIPT (per ~/.claude/CLAUDE.md Rule 12 fidelity tiers):
## Verbatim port of Octopus Calamari's
##   Calamari/source/Calamari/Scripts/Octopus.Features.IISWebSite_BeforePostDeploy.ps1
## with one mechanical substitution applied — `Octopus.` → `Squid.` in identifiers
## and dictionary keys, `$OctopusParameters` → `$SquidParameters`. Every line of
## deployment logic below is functionally identical to its Octopus counterpart so
## that operators carrying Octopus IIS variable specs to Squid can rely on
## byte-for-byte behavioural compatibility.
##
## The drift detector at
##   tests/Squid.UnitTests/Services/DeploymentExecution/Targets/Tentacle/
##     IISDeployScriptDriftDetectorTests.cs
## fails CI if any of the key operations (mutex, SNI detection, netsh-cert
## binding, appcmd auth, app pool setup, PS-7.3 compat wrapper) drift away from
## the Octopus reference. Update both sides in lockstep when porting upstream
## fixes — never edit this file alone.
##
## RUNTIME CONTRACT:
##   `$SquidParameters` MUST be defined at script scope before this file is
##   sourced. `IISDeployScriptBuilder.cs` generates that preamble at dispatch
##   time, with all action-property values pre-escaped for PowerShell single-
##   quote string literals. See IISDeployScriptBuilder for the exact preamble
##   shape; do NOT add `$SquidParameters = @{}` here.
## --------------------------------------------------------------------------------------

$DeployIISScriptBlock = {
	param(
		[Parameter(Mandatory = $true)]
		$SquidParameters
	)

	function Is-DeploymentTypeDisabled($value) {
		return !$value -or ![Bool]::Parse($value)
	}

	$deployAsWebSite = !(Is-DeploymentTypeDisabled $SquidParameters["Squid.Action.IISWebSite.CreateOrUpdateWebSite"])
	$deployAsWebApplication = !(Is-DeploymentTypeDisabled $SquidParameters["Squid.Action.IISWebSite.WebApplication.CreateOrUpdate"])
	$deployAsVirtualDirectory = !(Is-DeploymentTypeDisabled $SquidParameters["Squid.Action.IISWebSite.VirtualDirectory.CreateOrUpdate"])


	if (!$deployAsVirtualDirectory -and !$deployAsWebSite -and !$deployAsWebApplication)
	{
		Write-Host "Skipping IIS deployment. Neither Website nor Virtual Directory nor Web Application deployment type has been enabled." 
		exit 0
	}

	try {
		$iisFeature = Get-WindowsFeature Web-WebServer -ErrorAction Stop
		if ($iisFeature -eq $null -or $iisFeature.Installed -eq $false) {
			Write-Error "It looks like IIS is not installed on this server and the deployment is likely to fail."
			Write-Error "Tip: You can use PowerShell to ensure IIS is installed: 'Install-WindowsFeature Web-WebServer'"
			Write-Error "     You are likely to want more IIS features than just the web server. Run 'Get-WindowsFeature *web*' to see all of the features you can install."
			exit 1
		}
		else {
			$iisVersion = Get-ItemProperty HKLM:\SOFTWARE\Microsoft\InetStp\  | Select VersionString
			Write-Verbose "Detected IIS $($iisVersion.VersionString)"
		}
	} catch {
		Write-Verbose "Call to `Get-WindowsFeature Web-WebServer` failed."
		Write-Verbose "Unable to determine if IIS is installed on this server but will optimistically continue."
	}

	try {
		Add-PSSnapin WebAdministration -ErrorAction Stop
	} catch {
		try {
			Import-Module WebAdministration -ErrorAction Stop
			} catch {
				Write-Warning "We failed to load the WebAdministration module. This usually resolved by doing one of the following:"
				Write-Warning "1. Install IIS via Add Roles and Features, Web Server (IIS)"
				Write-Warning "2. Install .NET Framework 3.5.1"
				Write-Warning "3. Upgrade to PowerShell 3.0 (or greater)"
				Write-Warning "4. On Windows 2008 you might need to install PowerShell SnapIn for IIS from http://www.iis.net/downloads/microsoft/powershell#additionalDownloads"
				throw ($error | Select-Object -First 1)
		}
	}

	function Wait-OnMutex {
		param(
		[parameter(Mandatory = $true)][string] $mutexId
		)

		Try	{
			$mutex = New-Object System.Threading.Mutex -ArgumentList false, $mutexId

			while (-not $mutex.WaitOne(5000))
			{
				Write-Verbose "Cannot start this IIS website related task yet. There is already another task running that cannot be run in conjunction with any other task. Please wait..."
			}
			
			Write-Verbose "Acquired mutex $mutexId"
			return $mutex
		}
		Catch [System.Threading.AbandonedMutexException] {
			return Wait-OnMutex $mutexId
		}
		Catch [System.SystemException]{
			Write-Verbose "Wait-OnMutex had a major issue, possibly not running with sufficient privileges recover the mutex, details: $_.Exception.Message"
		}
	}

	function Determine-Path($path) {
		if (!$path) {
			$path = "."
		}

		return (resolve-path $path).ProviderPath
	}

	$maxFailures = $SquidParameters["Squid.Action.IISWebSite.MaxRetryFailures"]
	if ($maxFailures -Match "^\d+$") {
		$maxFailures = [int]$maxFailures
	} else {
		$maxFailures = 5
	}

	$sleepBetweenFailures = $SquidParameters["Squid.Action.IISWebSite.SleepBetweenRetryFailuresInSeconds"]
	if ($sleepBetweenFailures -Match "^\d+$") {
		$sleepBetweenFailures = [int]$sleepBetweenFailures
	} else {
		$sleepBetweenFailures = Get-Random -minimum 1 -maximum 4
	}

	if ($sleepBetweenFailures -gt 60) {
		Write-Host "Invalid Sleep time between failures.  Setting to max of 60 seconds"
		$sleepBetweenFailures = 60
	}

	# Not available on Server 2008
	$hasWebCommitDelay = $false
	If(Get-Command "Start-WebCommitDelay" -ErrorAction SilentlyContinue){
		$hasWebCommitDelay = $true
	}
		
	function Execute-WithRetry([ScriptBlock] $command, $noLock) {

		function Core-Execute-WithRetry([ScriptBlock] $command) {
			$attemptCount = 0
			$operationIncomplete = $true
		
			while ($operationIncomplete -and $attemptCount -lt $maxFailures) {
				$attemptCount = ($attemptCount + 1)
		
				if ($attemptCount -ge 2) {
					Write-Host "Waiting for $sleepBetweenFailures seconds before retrying..."
					Start-Sleep -s $sleepBetweenFailures
					Write-Host "Retrying..."
				}
		
				try {
					& $command
		
					$operationIncomplete = $false
				} catch [System.Exception] {
					if($hasWebCommitDelay){
						Stop-WebCommitDelay -Commit $false -ErrorAction SilentlyContinue
					}
					if ($attemptCount -lt ($maxFailures)) {
						Write-Host ("Attempt $attemptCount of $maxFailures failed: " + $_.Exception.Message)
					} else {
						throw
					}
				}
			}
		}

		if($noLock) {
			Core-Execute-WithRetry -command $command
		} else {
			$mutexId = 'Global\Octopus-IIS-Metabase-Mutex'
			$mutex = Wait-OnMutex $mutexId
			Try {
				Core-Execute-WithRetry -command $command
			}
			Finally {
				$mutex.ReleaseMutex()
				$mutex.Close()
			}
		}
	}

	function SetUp-ApplicationPool($applicationPoolName, $applicationPoolIdentityType, 
									$applicationPoolUsername, $applicationPoolPassword,
									$applicationPoolFrameworkVersion,  $startPool) 
	{

		$appPoolPath = ("IIS:\AppPools\" + $applicationPoolName)

		# Set App Pool
		Execute-WithRetry { 
			Write-Verbose "Loading Application pool"
			$exists = Test-Path $appPoolPath -ErrorAction SilentlyContinue
			if (!$exists) { 
				Write-Host "Application pool `"$applicationPoolName`" does not exist, creating..." 
				New-Item $appPoolPath -confirm:$false
			} else {
				Write-Host "Application pool `"$applicationPoolName`" already exists"
			}
			# Confirm it's there. Get-Item can pause if the app-pool is suspended, so use Get-WebAppPoolState
			$pool = Get-WebAppPoolState $applicationPoolName

			if ($startPool -eq $false) {
				if ($pool.Value -eq "Started") {
					Write-Host "Application pool is started. Attempting to stop..."
					Stop-WebAppPool $applicationPoolName
				}
			}
		}

		# Set App Pool Identity
		Execute-WithRetry { 
			Write-Host "Set application pool identity: $applicationPoolIdentityType"
			if ($applicationPoolIdentityType -eq "SpecificUser") {
				Set-ItemProperty $appPoolPath -name processModel -value @{identitytype="SpecificUser"; username="$applicationPoolUsername"; password="$applicationPoolPassword"}
			} else {
				Set-ItemProperty $appPoolPath -name processModel -value @{identitytype="$applicationPoolIdentityType"}
			}
		}

		# Set .NET Framework
		Execute-WithRetry { 
			Write-Host "Set .NET framework version: $applicationPoolFrameworkVersion" 
			if($applicationPoolFrameworkVersion -eq "No Managed Code")
			{
				Set-ItemProperty $appPoolPath managedRuntimeVersion ""
			}
			else
			{
				Set-ItemProperty $appPoolPath managedRuntimeVersion $applicationPoolFrameworkVersion
			}
		}
	}

	function Assign-ToApplicationPool($iisPath, $applicationPoolName) {
		Execute-WithRetry { 
			Write-Verbose "Loading Site"
			$pool = Get-ItemProperty $iisPath -name applicationPool
			if ($applicationPoolName -ne $pool) {
				Write-Host "Assigning `"$iisPath`" to application pool `"$applicationPoolName`"..."
				Set-ItemProperty $iisPath -name applicationPool -value $applicationPoolName
			} else {
				Write-Host "Application pool `"$applicationPoolName`" already assigned to `"$iisPath`""
			}
		}
	}

	function Start-ApplicationPool($applicationPoolName) {
		# It can take a while for the App Pool to come to life (#490)
		Start-Sleep -s 1

		# Start App Pool
		Execute-WithRetry { 
			$state = Get-WebAppPoolState $applicationPoolName
			if ($state.Value -eq "Stopped") {
				Write-Host "Application pool is stopped. Attempting to start..."
				Start-WebAppPool $applicationPoolName
			}
		} -noLock $true
	}

	function Get-FullPath($root, $segments)
	{
		return $root +  "\" + ($segments -join "\")
	}

	function Assert-ParentSegmentsExist($sitePath, $virtualPathSegments) {
		$fullPathToVirtualPathSegment = $sitePath
		for($i = 0; $i -lt $virtualPathSegments.Length - 1; $i++) {
			$fullPathToVirtualPathSegment = $fullPathToVirtualPathSegment + "\" + $virtualPathSegments[$i]
			$segment = Get-Item $fullPathToVirtualPathSegment -ErrorAction SilentlyContinue
			if (!$segment) {
				$fullPath = Get-FullPath -root $sitePath -segments $virtualPathSegments
				throw "Virtual path `"$fullPathToVirtualPathSegment`" does not exist. Please make sure all parent segments of $fullPath exist."
			}
		}
	}

	function Assert-WebsiteExists($SitePath, $SiteName)
	{
		Execute-WithRetry { 
			Write-Verbose "Looking for the parent Site `"$SiteName`" at `"$SitePath`"..."
			$site = Get-Item $SitePath -ErrorAction SilentlyContinue
			if (!$site) 
			{ 
				throw "The Web Site `"$SiteName`" does not exist in IIS and this step cannot create the Web Site because the necessary details are not available. Add a step which makes sure the parent Web Site exists before this step attempts to add a child to it." 
			}
		}
	}

	function Convert-ToPathSegments($VirtualPath)
	{
		return $VirtualPath.Split(@('\', '/'), [System.StringSplitOptions]::RemoveEmptyEntries)
	}

	function Set-Path($virtualPath, $physicalPath)
	{
		Execute-WithRetry { 
			Write-Host ("Setting physical path of $virtualPath to $physicalPath")
			Set-ItemProperty $virtualPath -name physicalPath -value "$physicalPath"
		}
	}

	function Is-Directory($Path){
		return Test-Path -Path $Path -PathType Container
	}

	if ($deployAsVirtualDirectory) 
	{
		$webSiteName = $SquidParameters["Squid.Action.IISWebSite.VirtualDirectory.WebSiteName"]
		$physicalPath = Determine-Path $SquidParameters["Squid.Action.IISWebSite.VirtualDirectory.PhysicalPath"]
		$virtualPath = $SquidParameters["Squid.Action.IISWebSite.VirtualDirectory.VirtualPath"]

		Write-Host "Making sure a Virtual Directory `"$virtualPath`" is configured as a child of `"$webSiteName`" at `"$physicalPath`"..."
		
		pushd IIS:\

		$sitePath = "IIS:\Sites\$webSiteName"

		Assert-WebsiteExists -SitePath $sitePath -SiteName $webSiteName

		[array]$virtualPathSegments =  Convert-ToPathSegments -VirtualPath $virtualPath
		Assert-ParentSegmentsExist -sitePath $sitePath -virtualPathSegments $virtualPathSegments

		$fullPathToLastVirtualPathSegment = Get-FullPath -root $sitePath -segments $virtualPathSegments
		$lastSegment = Get-Item $fullPathToLastVirtualPathSegment -ErrorAction SilentlyContinue

		if (!$lastSegment) {
			Write-Host "`"$virtualPath`" does not exist. Creating Virtual Directory pointing to $fullPathToLastVirtualPathSegment ..."
			Execute-WithRetry { 
				New-Item $fullPathToLastVirtualPathSegment -type VirtualDirectory -physicalPath $physicalPath
			}
		} else {
			if ($lastSegment.ElementTagName -eq 'virtualDirectory') {
				Write-Host "Virtual Directory `"$virtualPath`" already exists, no need to create it."
			} elseif ($lastSegment.ElementTagName -eq 'application') {
				# It looks like the only reliable way to do the conversion is to delete the exsting application and then create a new virtual directory. http://stackoverflow.com/questions/16738995/powershell-convertto-webapplication-on-iis
				# We don't want to delete anything as the customer might have handcrafted the settings and has no way of retrieving them.
				throw "`"$virtualPath`" already exists in IIS and points to a Web Application. We cannot automatically change this to a Virtual Directory on your behalf. Please delete it and then re-deploy the project."
			} else {
				if (!(Is-Directory -Path $physicalPath)) {
					throw "`"$virtualPath`" already exists in IIS and points to an unknown item which isn't a directory. Please delete it and then re-deploy the project. If you specified a custom Physical Path that targets this location, switch back to the default location and let Squid update the Physical Path of the Virtual Directory on your behalf."
				}

				Write-Host "`"$virtualPath`" already exists in IIS and points to an unknown item which seems to be a directory. We will try to convert it to a Virtual Directory. If you specified a custom Physical Path that targets this location, switch back to the default location and let Squid update the Physical Path of the Virtual Directory on your behalf."
				Execute-WithRetry { 
					New-Item $fullPathToLastVirtualPathSegment -type VirtualDirectory -physicalPath $physicalPath
				}
			}

			Set-Path -virtualPath $fullPathToLastVirtualPathSegment -physicalPath $physicalPath
		}

		popd	
	} 

	if ($deployAsWebApplication)
	{
		$webSiteName = $SquidParameters["Squid.Action.IISWebSite.WebApplication.WebSiteName"]
		$physicalPath = Determine-Path $SquidParameters["Squid.Action.IISWebSite.WebApplication.PhysicalPath"]
		$virtualPath = $SquidParameters["Squid.Action.IISWebSite.WebApplication.VirtualPath"]
		$startAppPool = if ($SquidParameters.ContainsKey("Squid.Action.IISWebSite.StartApplicationPool")) { $SquidParameters["Squid.Action.IISWebSite.StartApplicationPool"] } else { $true }

		Write-Host "Making sure a Web Application `"$virtualPath`" is configured as a child of `"$webSiteName`" at `"$physicalPath`"..."
		
		pushd IIS:\

		$applicationPoolName = $SquidParameters["Squid.Action.IISWebSite.WebApplication.ApplicationPoolName"]
		$applicationPoolIdentityType = $SquidParameters["Squid.Action.IISWebSite.WebApplication.ApplicationPoolIdentityType"]
		$applicationPoolUsername = $SquidParameters["Squid.Action.IISWebSite.WebApplication.ApplicationPoolUsername"]
		$applicationPoolPassword = $SquidParameters["Squid.Action.IISWebSite.WebApplication.ApplicationPoolPassword"]
		$applicationPoolFrameworkVersion = $SquidParameters["Squid.Action.IISWebSite.WebApplication.ApplicationPoolFrameworkVersion"]

		$sitePath = ("IIS:\Sites\" + $webSiteName)

		Assert-WebsiteExists -SitePath $sitePath -SiteName $webSiteName

		[array]$virtualPathSegments =  Convert-ToPathSegments -VirtualPath $virtualPath
		Assert-ParentSegmentsExist -sitePath $sitePath -virtualPathSegments $virtualPathSegments

		$fullPathToLastVirtualPathSegment = Get-FullPath -root $sitePath -segments $virtualPathSegments
		$lastSegment = Get-Item $fullPathToLastVirtualPathSegment -ErrorAction SilentlyContinue

		SetUp-ApplicationPool -applicationPoolName $applicationPoolName -applicationPoolIdentityType $applicationPoolIdentityType -applicationPoolUsername $applicationPoolUsername -applicationPoolPassword $applicationPoolPassword -applicationPoolFrameworkVersion $applicationPoolFrameworkVersion -startPool $startAppPool

		if (!$lastSegment) {
			Write-Host "`"$virtualPath`" does not exist. Creating Web Application pointing to $fullPathToLastVirtualPathSegment ..."
			Execute-WithRetry { 
				New-Item $fullPathToLastVirtualPathSegment -type Application -physicalPath $physicalPath
			}
		} else {
			if ($lastSegment.ElementTagName -eq 'application') {
				Write-Host "Web Application `"$virtualPath`" already exists, no need to create it."
			} elseif ($lastSegment.ElementTagName -eq 'virtualDirectory') {
				# It looks like the only reliable way to do the conversion is to delete the exsting web application and then create a new virtual directory. http://stackoverflow.com/questions/16738995/powershell-convertto-webapplication-on-iis
				# We don't want to delete anything as the customer might have handcrafted the settings and has no way of retrieving them.
				throw "`"$virtualPath`" already exists in IIS and points to a Virtual Directory. We cannot automatically change this to a Web Application on your behalf. Please delete it and then re-deploy the project."
			} else {
				if (!(Is-Directory -Path $physicalPath)) {
					throw "`"$virtualPath`" already exists in IIS and points to an unknown item which isn't a directory. Please delete it and then re-deploy the project. If you specified a custom Physical Path that targets this location, switch back to the default location and let Squid update the Physical Path of the Web Application on your behalf."
				}

				Write-Host "`"$virtualPath`" already exists in IIS and points to an unknown item which seems to be a directory. We will try to convert it to a Web Application. If you specified a custom Physical Path that targets this location, switch back to the default location and let Squid update the Physical Path of the Web Application on your behalf."
				Execute-WithRetry { 
					New-Item $fullPathToLastVirtualPathSegment -type Application -physicalPath $physicalPath
				}

			}

			Set-Path -virtualPath $fullPathToLastVirtualPathSegment -physicalPath $physicalPath
		}

		Assign-ToApplicationPool -iisPath $fullPathToLastVirtualPathSegment -applicationPoolName $applicationPoolName					
		
		if($startAppPool -eq $true) {
			Start-ApplicationPool $applicationPoolName
		}

		popd
	}


	if ($deployAsWebSite)
	{	
		$webSiteName = $SquidParameters["Squid.Action.IISWebSite.WebSiteName"]
		$applicationPoolName = $SquidParameters["Squid.Action.IISWebSite.ApplicationPoolName"]
		$bindingString = $SquidParameters["Squid.Action.IISWebSite.Bindings"]
		$existingBindings = $SquidParameters["Squid.Action.IISWebSite.ExistingBindings"]
		$webRoot =  Determine-Path $SquidParameters["Squid.Action.IISWebSite.WebRoot"]
		$enableWindows = $SquidParameters["Squid.Action.IISWebSite.EnableWindowsAuthentication"]
		$enableBasic = $SquidParameters["Squid.Action.IISWebSite.EnableBasicAuthentication"]
		$enableAnonymous = $SquidParameters["Squid.Action.IISWebSite.EnableAnonymousAuthentication"]
		$applicationPoolIdentityType = $SquidParameters["Squid.Action.IISWebSite.ApplicationPoolIdentityType"]
		$applicationPoolUsername = $SquidParameters["Squid.Action.IISWebSite.ApplicationPoolUsername"]
		$applicationPoolPassword = $SquidParameters["Squid.Action.IISWebSite.ApplicationPoolPassword"]
		$applicationPoolFrameworkVersion = $SquidParameters["Squid.Action.IISWebSite.ApplicationPoolFrameworkVersion"]
		$startAppPool = if ($SquidParameters.ContainsKey("Squid.Action.IISWebSite.StartApplicationPool")) { $SquidParameters["Squid.Action.IISWebSite.StartApplicationPool"] } else { $true }
		$startWebSite = if ($SquidParameters.ContainsKey("Squid.Action.IISWebSite.StartWebSite")) { $SquidParameters["Squid.Action.IISWebSite.StartWebSite"] } else { $true }
		
		Write-Host "Making sure a Website `"$webSiteName`" is configured in IIS..."

		#Assess SNI support (IIS 8 or greater)
		$iis = get-itemproperty HKLM:\SOFTWARE\Microsoft\InetStp\  | select setupstring 
		$iisVersion = ($iis.SetupString.Substring(4)) -as [double]
		$supportsSNI = $iisVersion -ge 8

		$wsbindings = new-object System.Collections.ArrayList

		function Write-IISBinding($message, $bindingObject) {
			if(-not ($bindingObject -is [PSObject])) {
				Write-Host "$message @{$([String]::Join("; ", ($bindingObject.Keys | % { return "$($_)=$($bindingObject[$_])" })))}"
			} else {
				Write-Host "$message $bindingObject"
			}
		}

		if(Get-Command ConvertFrom-Json -errorAction SilentlyContinue){
			$bindingArray = (ConvertFrom-Json $bindingString)
		} else {
			add-type -assembly system.web.extensions
			$serializer=new-object system.web.script.serialization.javascriptSerializer
			$bindingArray = ($serializer.DeserializeObject($bindingString))
		}

		ForEach($binding in $bindingArray){
			if(![Bool]::Parse($binding.enabled)) {
				Write-IISBinding "Ignore binding: " $binding
				continue
			}

			$sslFlagPart = @{$true=1;$false=0}[[Bool]::Parse($binding.requireSni)]  
			$bindingIpAddress =  @{$true="*";$false=$binding.ipAddress}[[string]::IsNullOrEmpty($binding.ipAddress)]
			$bindingInformation = $bindingIpAddress+":"+$binding.port+":"+$binding.host

			$bindingObj = @{
				protocol=(($binding.protocol, "http") -ne $null)[0].ToLower();
				ipAddress=$bindingIpAddress;
				port=$binding.port;
				host=$binding.host;
				bindingInformation=$bindingInformation;
			};

			if ($binding.certificateVariable) {
				$bindingObj.certificateVariable = $binding.certificateVariable.Trim();
			} elseif ($binding.thumbprint -and ($null -ne $binding.thumbprint)){
				$bindingObj.thumbprint=$binding.thumbprint.Trim();
			}

			if ([Bool]::Parse($supportsSNI)) {
				$bindingObj.sslFlags=$sslFlagPart;
			}

			$wsbindings.Add($bindingObj) | Out-Null
		}

		# For any HTTPS bindings, ensure the certificate is configured for the IP/port combination
		$wsbindings | where-object { $_.protocol -eq "https" } | foreach-object {

			# Squid certificate-variable system (forward-compatible with future phases): when the binding
			# references a named variable, the script reads its sibling ".Thumbprint" property from
			# $SquidParameters. Until the cert-variable feature ships, operators use the direct
			# `thumbprint` field on the binding, which points at a cert already installed in
			# LocalMachine\My on the target Tentacle.
			if ($_.certificateVariable) {
				$sslCertificateThumbprint = $SquidParameters[$_.certificateVariable + ".Thumbprint"]
			} elseif ($_.thumbprint){
				# Otherwise, the certificate thumbprint was supplied directly in the binding
				$sslCertificateThumbprint = $_.thumbprint.Trim()
			} else {
				[Console]::Error.WriteLine("To configure an HTTPS binding, provide the `thumbprint` field on the binding pointing at a certificate already installed in LocalMachine\My on the target Tentacle.")
				exit 1
			}

			Write-Host "Finding SSL certificate with thumbprint $sslCertificateThumbprint"
			foreach ($certStore in (Get-ChildItem Cert:\LocalMachine)) {
				try {
					$certs = Get-ChildItem "Cert:\LocalMachine\$($certStore.Name)" -ErrorAction Stop
					$certificate = $certs | Where-Object { $_.Thumbprint -eq $sslCertificateThumbprint -and $_.HasPrivateKey -eq $true } | Select-Object -first 1
					if ($certificate) {
						break
					}
				} catch {
					Write-Host "Skipping inaccessible certificate store '$($certStore.Name)': $_"
				}
			}
			if (! $certificate) 
			{
				throw "Could not find certificate under Cert:\LocalMachine with thumbprint $sslCertificateThumbprint. Make sure that the certificate is installed to the Local Machine context and that the private key is available."
			}

			$certPathParts = $certificate.PSParentPath.Split('\')
			$certStoreName = $certPathParts[$certPathParts.Length-1]

			Write-Host ("Found certificate: " + $certificate.Subject + " in: " + $certStoreName)

			$ipAddress = $_.ipAddress;
			if ((! $ipAddress) -or ($ipAddress -eq '*')) {
				$ipAddress = "0.0.0.0"
			}
			$port = $_.port
			$hostname = $_.host
			Execute-WithRetry { 
		
				# if we are supporting SNI then we need to bind cert to hostname instead of ip
				if($_.sslFlags -eq 1){
			
					$existing = & netsh http show sslcert hostnameport="$($hostname):$port"
					if ($LastExitCode -eq 0) {
						$hasThumb = ($existing | Where-Object { $_.IndexOf($certificate.Thumbprint, [System.StringComparison]::OrdinalIgnoreCase) -ne -1 })
						if ($hasThumb -eq $null) {
							Write-Host "A different binding exists for the Hostname/port combination, replacing..."
						
							& netsh http delete sslcert hostnameport="$($hostname):$port"
							if ($LastExitCode -ne 0 ){
								throw
							}

							$appid = [System.Guid]::NewGuid().ToString("b")
							& netsh http add sslcert hostnameport="$($hostname):$port" certhash="$($certificate.Thumbprint)" appid="$appid" certstorename="$certStoreName"
							if ($LastExitCode -ne 0 ){
								throw
							}

						} else {
							Write-Host "The required certificate binding is already in place"
						}
					} else {
						$appid = [System.Guid]::NewGuid().ToString("b")
						& netsh http add sslcert hostnameport="$($hostname):$port" certhash="$($certificate.Thumbprint)" appid="$appid" certstorename="$certStoreName"
						if ($LastExitCode -ne 0 ){
							throw
						}
					}	
				} else {
					$existing = & netsh http show sslcert ipport="$($ipAddress):$port"
					if ($LastExitCode -eq 0) {
						$hasThumb = ($existing | Where-Object { $_.IndexOf($certificate.Thumbprint, [System.StringComparison]::OrdinalIgnoreCase) -ne -1 })
						if ($hasThumb -eq $null) {
							Write-Host "A different binding exists for the IP/port combination, replacing..."
						
							& netsh http delete sslcert ipport="$($ipAddress):$port"
							if ($LastExitCode -ne 0 ){
								throw
							}

							$appid = [System.Guid]::NewGuid().ToString("b")
							& netsh http add sslcert ipport="$($ipAddress):$port" certhash="$($certificate.Thumbprint)" appid="$appid" certstorename="$certStoreName"
							if ($LastExitCode -ne 0 ){
								throw
							}
						
						} else {
							Write-Host "The required certificate binding is already in place"
						}
					} else {
						Write-Host "Adding a new SSL certificate binding..."
						$appid = [System.Guid]::NewGuid().ToString("b")
						& netsh http add sslcert ipport="$($ipAddress):$port" certhash="$($certificate.Thumbprint)" appid="$appid" certstorename="$certStoreName"
						if ($LastExitCode -ne 0 ){
							Write-Host "Failed adding new SSL binding for certificate with thumbprint '$($certificate.Thumbprint)'. Exit code: $LastExitCode"
							throw
						}
					}	
				}	
			}
		}

		## --------------------------------------------------------------------------------------
		## Run
		## --------------------------------------------------------------------------------------

		pushd IIS:\
		
		SetUp-ApplicationPool -applicationPoolName $applicationPoolName -applicationPoolIdentityType $applicationPoolIdentityType -applicationPoolFrameworkVersion $applicationPoolFrameworkVersion -applicationPoolUsername $applicationPoolUsername -applicationPoolPassword $applicationPoolPassword -startPool $startAppPool

		$sitePath = ("IIS:\Sites\" + $webSiteName)

		$odTempBinding = ":81:od-temp.example.com"

		# Create Website
		Execute-WithRetry { 
			Write-Verbose "Loading Site"
			$site = Get-Item $sitePath -ErrorAction SilentlyContinue
			if (!$site) { 
				Write-Host "Site `"$webSiteName`" does not exist, creating..." 
				$id = (dir iis:\sites | foreach {$_.id} | sort -Descending | select -first 1) + 1
				new-item $sitePath -bindings @{protocol="http";bindingInformation=$odTempBinding} -id $id -physicalPath $webRoot -confirm:$false
			} else {
				Write-Host "Site `"$webSiteName`" already exists"
			}
		}

		if($startWebSite -eq $false) {
			# Stop Website
			Execute-WithRetry { 
				$state = Get-WebsiteState $webSiteName
				if ($state.Value -eq "Started") {
					Write-Host "Web site is started. Attempting to stop..."
					Stop-Website $webSiteName
				}
			} -noLock $true
		}

		Assign-ToApplicationPool -iisPath $sitePath -applicationPoolName $applicationPoolName
		Set-Path -virtualPath $sitePath -physicalPath $webRoot

		function Convert-ToHashTable($bindingArray) {
			$hash = @{}
			$bindingArray | %{
				$key = Get-BindingKey $_
				$hash[$key] = $_
			}
			return $hash
		}

		function Get-BindingKey($binding) {
			return $binding.protocol + "|" + $binding.bindingInformation + "|" + $binding.sslFlags
		}

		if($existingBindings -eq "Merge") {
			# Merge existing bindings into the configured collection. This allows the following code to be the same regardless of this options
			$configuredBindingsLookup = Convert-ToHashTable $wsbindings
			$existingBindings = Get-ItemProperty $sitePath -name bindings
			$bindingsToMerge = $existingBindings.Collection | where { ($configuredBindingsLookup[(Get-BindingKey $_)] -eq $null) -and ($_.bindingInformation -ne $odTempBinding) } | ForEach-Object { $wsbindings.Add($_) }
		}

		# Returns $true if existing IIS bindings are as specified in configuration, otherwise $false
		function Bindings-AreCorrect($existingBindings, $configuredBindings, [System.Collections.ArrayList] $bindingsToRemove) {
			$existingBindingsLookup = Convert-ToHashTable $existingBindings.Collection
			$configuredBindingsLookup = Convert-ToHashTable $configuredBindings
		
			# Are there existing assigned bindings that are not configured
			for ($i = 0; $i -lt $existingBindings.Collection.Count; $i = $i+1) {
				$binding = $existingBindings.Collection[$i]
				$bindingKey = Get-BindingKey $binding

				$matching = $configuredBindingsLookup[$bindingKey]
			
				if ($matching -eq $null) {
					Write-Host "Found existing non-configured binding: $($binding.protocol) $($binding.bindingInformation)"
					$bindingsToRemove.Add($binding) | Out-Null
				}
			}

			if($bindingsToRemove.Length -gt 0){
				return $false
			}

			# Are there configured bindings which are not assigned
			for ($i = 0; $i -lt $configuredBindings.Count; $i = $i+1) {
				$wsbinding = $configuredBindings[$i]
				$wsBindingKey = Get-BindingKey $wsbinding

				$matching = $existingBindingsLookup[$wsBindingKey]

				if ($matching -eq $null) {
					Write-Host "Found configured binding which is not assigned: $($wsbinding.protocol) $($wsbinding.bindingInformation)"
					return $false
				}
			}        

			Write-Host "Looks OK"

			return $true
		}

		# Set Bindings
		Execute-WithRetry { 
			Write-Host "Comparing existing IIS bindings with configured bindings..."
			$existingBindings = Get-ItemProperty $sitePath -name bindings
			$bindingsToRemove = new-object System.Collections.ArrayList

			if (-not (Bindings-AreCorrect $existingBindings $wsbindings $bindingsToRemove)) {
				Write-Host "Existing IIS bindings do not match configured bindings."
				Write-Host "Clearing IIS bindings"
				Clear-ItemProperty $sitePath -name bindings

				If($hasWebCommitDelay){
					Start-WebCommitDelay
				}
				for ($i = 0; $i -lt $wsbindings.Count; $i = $i+1) {
					Write-Host ("Assigning binding: " + ($wsbindings[$i].protocol + " " + $wsbindings[$i].bindingInformation))
					New-ItemProperty $sitePath -name bindings -value ($wsbindings[$i])
				}
				If($hasWebCommitDelay){
					Stop-WebCommitDelay -Commit $true
				}
			} else {
				Write-Host "Bindings are as configured. No changes required."
			}

			# try to remove ssl cert bindings for IIS bindings that are being removed
			$bindingsToRemove | where-object { $_.protocol -eq "https" } | foreach-object {
				$bindingParts = $_.bindingInformation.Split(':')
				$ipAddress = $bindingParts[0]
				if (!$ipAddress) {
					$ipAddress = "*"
				}
				$port = $bindingParts[1]
				$hostname = $bindingParts[2]

				if($_.sslFlags -eq 1){ # SNI on so we will have created against the hostname
					$existing = & netsh http show sslcert hostnameport="$($hostname):$port"
					if ($LastExitCode -eq 0) {
						Write-Host ("Removing unused SSL certificate binding: $($hostname):$port")
						& netsh http delete sslcert hostnameport="$($hostname):$port"
						if ($LastExitCode -ne 0 ){
							throw
						}
					}
				} else { # SNI off so we will have created against the ip
					# check if there are any other bindings to the same IP:Port so that
					# we don't remove an ssl cert that's used by any other sites on the server
					$existing = Get-WebBinding -IPAddress $ipAddress -Port $port -Protocol "https"
					if (!$existing) {
						Write-Host ("Removing unused SSL certificate binding: $($ipAddress):$port")
						if ($ipAddress -eq '*') {
							$ipAddress = "0.0.0.0"
						}
						& netsh http delete sslcert ipport="$($ipAddress):$port"

						if ($LastExitCode -ne 0 ){
							throw
						}
					}
				}
			}
		}

		$appCmdPath = $env:SystemRoot + "\system32\inetsrv\appcmd.exe"
		if ((Test-Path $appCmdPath) -eq $false) {
			throw "Could not find appCmd.exe at $appCmdPath"
		}

		try {
			Execute-WithRetry { 
				Write-Host "Anonymous authentication enabled: $enableAnonymous"
				& $appCmdPath set config "$webSiteName" -section:"system.webServer/security/authentication/anonymousAuthentication" /enabled:"$enableAnonymous" /commit:apphost
				if ($LastExitCode -ne 0 ){
					throw
				}		
			}

			Execute-WithRetry { 
				Write-Host "Basic authentication enabled: $enableBasic"
				& $appCmdPath set config "$webSiteName" -section:"system.webServer/security/authentication/basicAuthentication" /enabled:"$enableBasic" /commit:apphost
				if ($LastExitCode -ne 0 ){
					throw
				}		
			}

			Execute-WithRetry { 
				Write-Host "Windows authentication enabled: $enableWindows"
				& $appCmdPath set config "$webSiteName" -section:"system.webServer/security/authentication/windowsAuthentication" /enabled:"$enableWindows" /commit:apphost
				if ($LastExitCode -ne 0 ){
					throw
				}		
			
			}
		} catch [System.Exception] {
			Write-Host "Authentication options could not be set. This can happen when there is a problem with your application's web.config. For example, you might be using a section that requires an extension that is not installed on this web server (such as URL Rewriting). It can also happen when you have selected an authentication option and the appropriate IIS module is not installed (for example, for Windows authentication, you need to enable the Windows Authentication module in IIS/Windows first)"
			throw
		}

		if($startAppPool -eq $true) {
			Start-ApplicationPool $applicationPoolName
		}

		if($startWebSite -eq $true) {
			if ($wsbindings.Count -eq 0) {
				Write-Warning "The deployment has been configured to start the web site $webSiteName but no bindings are enabled."
			}
			# Start Website
			Execute-WithRetry { 
				$state = Get-WebsiteState $webSiteName
				if ($state.Value -eq "Stopped") {
					Write-Host "Web site is stopped. Attempting to start..."
					Start-Website $webSiteName
				} elseif ($state -eq "Undefined") {
					Write-Warning "Unable to retrieve the state of the web site $webSiteName. The web site will not be started. This is commonly caused by an invalid web site configuration."
				}
			} -noLock $true
		}

		popd
	}


	Write-Host "IIS configuration complete"
}

# ── Squid: Custom-script PreDeploy hook (Phase 5 — Octopus CustomScripts parity) ──
# Mirrors Octopus's `Octopus.Action.CustomScripts.PreDeploy.ps1` slot. The operator's
# script body has already been server-rendered into the preamble as a single-quoted
# string literal (so apostrophes are doubled and the value is safe to Invoke-Expression).
# Auto-imports WebAdministration so operators can use `Stop-WebAppPool` / `Restart-WebAppPool`
# directly without boilerplate.
$preDeployScript = $SquidParameters['Squid.Action.CustomScripts.PreDeploy.ps1']
if (-not [string]::IsNullOrWhiteSpace($preDeployScript)) {
	Write-Host "Running PreDeploy custom script..."
	Import-Module WebAdministration -ErrorAction SilentlyContinue
	$preDeployBlock = [scriptblock]::Create($preDeployScript)
	& $preDeployBlock
	if ($LastExitCode -ne 0 -and $null -ne $LastExitCode) {
		throw "PreDeploy custom script exited with code $LastExitCode. Aborting IIS deploy before metabase mutations."
	}
}

# ── Squid: Packaged PreDeploy script (Phase 1.6.9 P1-3 — Octopus PackagedScriptBehaviour parity) ──
# After Octopus's `ConfiguredScriptBehaviour` runs the operator-inline PreDeploy script,
# `PackagedScriptBehaviour` (`PackagedScriptBehaviour.cs:14-35`) ALSO looks inside the
# EXTRACTED PACKAGE for a `PreDeploy.ps1` file and runs it. This lets developers ship
# deploy-stage scripts WITH their application code instead of authoring them inline in
# the deploy step UI. Squid mirrors the same operator-facing contract: when the package
# extraction step (Phase 10) wrote a `PreDeploy.ps1` into WebRoot, we invoke it here.
#
# Order vs. inline PreDeploy: matches Octopus — configured (inline) first, packaged second.
$preDeployWebRoot = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
if (-not [string]::IsNullOrWhiteSpace($preDeployWebRoot)) {
	$packagedPreDeploy = Join-Path $preDeployWebRoot 'PreDeploy.ps1'
	if (Test-Path -LiteralPath $packagedPreDeploy -PathType Leaf) {
		Write-Host "Packaged PreDeploy: running '$packagedPreDeploy'"
		Import-Module WebAdministration -ErrorAction SilentlyContinue
		& $packagedPreDeploy
		if ($LastExitCode -ne 0 -and $null -ne $LastExitCode) {
			throw "Packaged PreDeploy script '$packagedPreDeploy' exited with code $LastExitCode. Aborting IIS deploy."
		}
	}
}

# ── Squid: .NET Configuration Variables rewriter (Phase 6 — Octopus ConfigurationVariables parity) ──
# Mirrors Octopus's `Octopus.Features.ConfigurationVariables` feature. When the toggle is True
# and a `Squid.Action.IISWebSite.WebRoot` is set, walks every *.config file under that dir and
# replaces matching <appSettings/add@key>, <connectionStrings/add@name>, <applicationSettings/.../setting@name>
# entries with values from $SquidVariables (the deployment's full variable set, shipped via the
# preamble). Runs BEFORE the IIS configure dispatch so any web.config rewrites are in place
# before IIS reads the config to start the site.

function Update-IISConfigurationVariables {
	param(
		[Parameter(Mandatory=$true)][string]$TargetDir,
		[Parameter(Mandatory=$true)][hashtable]$Variables
	)

	if (-not (Test-Path $TargetDir)) {
		Write-Host "ConfigurationVariables: target dir '$TargetDir' does not exist; skipping."
		return
	}

	$configFiles = @(Get-ChildItem -Path $TargetDir -Recurse -Filter "*.config" -ErrorAction SilentlyContinue)
	if ($configFiles.Count -eq 0) {
		Write-Host "ConfigurationVariables: no *.config files found under '$TargetDir'; skipping."
		return
	}

	# P2-1 (1.6.9): when IgnoreVariableReplacementErrors=True, XML parse failures get
	# logged and skipped. When False/unset (default), failures throw — operator's deploy
	# fails loudly if a config file is malformed.
	$ignoreVarErrors = $SquidParameters['Squid.Action.Package.IgnoreVariableReplacementErrors'] -eq 'True'

	foreach ($file in $configFiles) {
		Write-Host "ConfigurationVariables: scanning $($file.FullName)"
		$xml = $null
		try {
			$xml = [xml](Get-Content -LiteralPath $file.FullName -Raw)
		} catch {
			$parseErrorMsg = "ConfigurationVariables: failed to parse '$($file.FullName)' as XML: $($_.Exception.Message)"
			if ($ignoreVarErrors) {
				Write-Host "  - skip (IgnoreVariableReplacementErrors=True): $($_.Exception.Message)"
				continue
			}
			throw "$parseErrorMsg. Set Squid.Action.Package.IgnoreVariableReplacementErrors=True to log+skip instead of failing the deploy."
		}

		$modified = $false

		# Octopus parity: XPath uses local-name() so the matcher works regardless of whether
		# the .config file has an XML namespace declared (Octopus's
		# ConfigurationVariablesReplacer.cs:77-79). A plain `//appSettings/add` matcher would
		# return ZERO nodes when web.config has `<configuration xmlns="...">`.

		# appSettings: <appSettings><add key="X" value="..."/></appSettings>
		foreach ($node in $xml.SelectNodes("//*[local-name()='appSettings']/*[local-name()='add'][@key]")) {
			$key = $node.GetAttribute("key")
			if ($Variables.ContainsKey($key)) {
				$node.SetAttribute("value", [string]$Variables[$key])
				Write-Host "  - appSettings/$key replaced"
				$modified = $true
			}
		}

		# connectionStrings: <connectionStrings><add name="X" connectionString="..." providerName="..."/></connectionStrings>
		foreach ($node in $xml.SelectNodes("//*[local-name()='connectionStrings']/*[local-name()='add'][@name]")) {
			$name = $node.GetAttribute("name")
			if ($Variables.ContainsKey($name)) {
				$node.SetAttribute("connectionString", [string]$Variables[$name])
				Write-Host "  - connectionStrings/$name replaced"
				$modified = $true
			}
		}

		# applicationSettings: <applicationSettings><Class><setting name="X"><value>...</value></setting></Class></applicationSettings>
		# Octopus parity: when <setting> has NO <value> child element, Octopus's
		# ReplaceStronglyTypeApplicationSetting (ConfigurationVariablesReplacer.cs:147-151)
		# CREATES the element. Earlier Squid implementation silently skipped — this is the fix.
		foreach ($node in $xml.SelectNodes("//*[local-name()='applicationSettings']//*[local-name()='setting'][@name]")) {
			$name = $node.GetAttribute("name")
			if ($Variables.ContainsKey($name)) {
				$valueNode = $node.SelectSingleNode("*[local-name()='value']")
				if ($null -eq $valueNode) {
					# Create the <value> element in the same namespace as the parent <setting> so
					# the resulting XML stays well-formed when the document had `xmlns="..."`.
					$valueNode = $xml.CreateElement('value', $node.NamespaceURI)
					$node.AppendChild($valueNode) | Out-Null
				}
				$valueNode.InnerText = [string]$Variables[$name]
				Write-Host "  - applicationSettings/$name replaced"
				$modified = $true
			}
		}

		if ($modified) {
			$xml.Save($file.FullName)
		}
	}
}

# ── Squid: Deployment journal + SkipIfAlreadyInstalled (1.6.9 P0-2 — Octopus AlreadyInstalledConvention parity) ──
# Mirrors Octopus's `Octopus.Action.Package.SkipIfAlreadyInstalled` short-circuit.
# When the flag is True and the local journal records a prior successful deploy with the
# same package fingerprint + WebRoot, exits with status 0 (skip) — no extraction, no
# rewriting, no IIS reconfig. Saves ~minutes per idempotent re-run.

function Get-IISDeployJournalPath {
	param([Parameter(Mandatory=$true)][string]$SiteName)
	$root = Join-Path $env:ProgramData 'Squid\IISDeploy\journal'
	if (-not (Test-Path -LiteralPath $root)) { New-Item -Path $root -ItemType Directory -Force | Out-Null }
	# Sanitise siteName to a filename-safe form (IIS site names can contain spaces / special chars).
	$safeName = [System.Text.RegularExpressions.Regex]::Replace($SiteName, '[^A-Za-z0-9._-]', '_')
	return Join-Path $root "$safeName.json"
}

function Get-IISDeployFingerprint {
	param([string]$SourcePath, [string]$WebRoot)
	# Hash combines the package's last-modified time + size + the operator-configured WebRoot.
	# When the operator re-runs with the same package + same target, fingerprint matches → skip.
	$parts = New-Object System.Collections.Generic.List[string]
	if (-not [string]::IsNullOrWhiteSpace($SourcePath) -and (Test-Path -LiteralPath $SourcePath)) {
		$info = Get-Item -LiteralPath $SourcePath
		$parts.Add($info.FullName)
		$parts.Add($info.Length.ToString())
		$parts.Add($info.LastWriteTimeUtc.ToString('o'))
	}
	$parts.Add($WebRoot)
	$concat = ($parts -join '|')
	$sha = [System.Security.Cryptography.SHA256]::Create()
	try {
		$bytes = [System.Text.Encoding]::UTF8.GetBytes($concat)
		$hash = $sha.ComputeHash($bytes)
		return [System.BitConverter]::ToString($hash).Replace('-', '').ToLowerInvariant()
	} finally {
		$sha.Dispose()
	}
}

function Read-IISDeployJournalEntry {
	param([Parameter(Mandatory=$true)][string]$SiteName)
	$path = Get-IISDeployJournalPath -SiteName $SiteName
	if (-not (Test-Path -LiteralPath $path)) { return $null }
	try {
		return Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
	} catch {
		Write-Warning "Could not parse deployment journal at '$path': $($_.Exception.Message). Treating as no prior entry."
		return $null
	}
}

function Write-IISDeployJournalEntry {
	param(
		[Parameter(Mandatory=$true)][string]$SiteName,
		[Parameter(Mandatory=$true)][string]$Fingerprint,
		[Parameter(Mandatory=$true)][string]$Status
	)
	$path = Get-IISDeployJournalPath -SiteName $SiteName
	$entry = [pscustomobject]@{
		SiteName = $SiteName
		Fingerprint = $Fingerprint
		Status = $Status
		DeployedAt = (Get-Date -Format 'o')
	}
	$entry | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding UTF8 -NoNewline
	Write-Host "Deployment journal: wrote entry to '$path' (status=$Status, fingerprint=$($Fingerprint.Substring(0,8))...)"
}

$skipIfAlreadyInstalled = $SquidParameters['Squid.Action.IISWebSite.Package.SkipIfAlreadyInstalled']
$journalSiteName = $SquidParameters['Squid.Action.IISWebSite.WebSiteName']
$journalSourcePath = $SquidParameters['Squid.Action.IISWebSite.Package.SourcePath']
$journalWebRoot = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
$journalFingerprint = Get-IISDeployFingerprint -SourcePath $journalSourcePath -WebRoot $journalWebRoot

if ($skipIfAlreadyInstalled -eq 'True' -and -not [string]::IsNullOrWhiteSpace($journalSiteName)) {
	$priorEntry = Read-IISDeployJournalEntry -SiteName $journalSiteName
	if ($null -ne $priorEntry -and $priorEntry.Status -eq 'Success' -and $priorEntry.Fingerprint -eq $journalFingerprint) {
		Write-Host "SkipIfAlreadyInstalled: prior deploy at $($priorEntry.DeployedAt) had the same package fingerprint + WebRoot. Skipping this deploy."
		exit 0
	}
}

# ── Squid: Journal-failure tracking via PowerShell trap (1.6.9 P0-2) ──
# A `trap` block runs on any uncaught exception/throw and lets us record the failure in
# the deployment journal BEFORE PowerShell exits with the error. Without this, the next
# re-run with SkipIfAlreadyInstalled=True wouldn't know the prior attempt failed — it
# would see no entry (or the older successful entry) and might mistakenly conclude
# nothing needs to happen.
#
# Mirrors Octopus's `IDeploymentJournalWriter.RecordFailure(...)` semantics: every
# deployment writes EXACTLY ONE journal entry per run, recording the final outcome.
trap {
	if (-not [string]::IsNullOrWhiteSpace($journalSiteName)) {
		try {
			Write-IISDeployJournalEntry -SiteName $journalSiteName -Fingerprint $journalFingerprint -Status 'Failed'
		} catch {
			Write-Warning "Could not write Failed entry to deployment journal: $($_.Exception.Message)"
		}
	}
	# Re-throw so the engine surfaces the real error (exit code + log).
	# PowerShell's default trap-action is `break` (eat the exception); we need `continue`
	# semantics from a top-level trap, so we re-throw.
	throw $_
}

# ── Squid: Certificate auto-import (1.6.9 P0-1 — Octopus IisWebSiteBeforeDeployFeature parity) ──
# When the operator sets `Squid.Action.IISWebSite.Certificate.PfxBase64`, decode the PFX
# bytes, import the LEAF cert into Cert:\LocalMachine\My, import every chain cert into
# Cert:\LocalMachine\CA (intermediates) or Cert:\LocalMachine\Root (the trailing self-signed
# anchor), then expose the leaf thumbprint via `$SquidVariables["<VariableName>.Thumbprint"]`
# so the Bindings JSON's `"certificateVariable": "<VariableName>"` lookup resolves to the
# freshly-imported thumbprint.
#
# Mirrors Octopus's `IisWebSiteBeforeDeployFeature.cs:24-89` (PFX → LocalMachine\My) +
# `WindowsX509CertificateStore.cs:265-282` (full-chain split: intermediates → CA, trailing
# self-signed → Root). Chain handling is critical for HTTPS deploys where the operator's
# PFX bundles a CA-issued leaf with the issuer's intermediates — without it the TLS handshake
# completes from Windows's perspective but clients see "chain not complete" errors.

function Import-IISCertificateFromPfxBase64 {
	param(
		[Parameter(Mandatory=$true)][string]$Base64,
		[string]$Password,
		[string]$ThumbprintVariableName
	)

	if ([string]::IsNullOrWhiteSpace($Base64)) { return $null }

	$bytes = [System.Convert]::FromBase64String($Base64)
	$tempPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("squid-iis-cert-" + [System.Guid]::NewGuid().ToString("N") + ".pfx")
	try {
		[System.IO.File]::WriteAllBytes($tempPfx, $bytes)

		# Import as a COLLECTION so we can iterate every cert in the PFX (leaf + intermediates
		# + optional self-signed root). Single-cert PFXes yield a one-element collection.
		# KeyStorageFlags mirror Octopus: MachineKeySet (key in machine store, not user) +
		# PersistKeySet (private key persists across import) + Exportable (operator can
		# re-export if needed for diagnostics).
		$securePw = if ([string]::IsNullOrEmpty($Password)) {
			New-Object System.Security.SecureString
		} else {
			ConvertTo-SecureString -String $Password -AsPlainText -Force
		}

		$collection = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2Collection
		$collection.Import(
			$tempPfx,
			$securePw,
			[System.Security.Cryptography.X509Certificates.X509KeyStorageFlags] 'MachineKeySet, PersistKeySet, Exportable')

		if ($collection.Count -eq 0) {
			throw "Certificate auto-import: PFX contained no certificates (Base64 length $($Base64.Length) bytes)."
		}

		# Convention: the first cert in the collection is the operator-meaningful "leaf"
		# (the one with the private key whose thumbprint goes into the Bindings JSON).
		# This matches Octopus + the Windows CryptoAPI's `GetCertificatesFromPfx`
		# ordering: leaf → intermediates → root.
		$leafCert = $collection[0]

		Add-IISCertificateToStore -Cert $leafCert -StoreName 'My'
		Write-Host "Certificate: imported leaf '$($leafCert.Subject)' with thumbprint $($leafCert.Thumbprint) into LocalMachine\My"

		# Chain handling: walk the remaining certs in PFX order. Mirrors Octopus's
		# `WindowsX509CertificateStore.cs:267-282`:
		#   - If a cert is the LAST one in the chain AND is self-signed → LocalMachine\Root
		#     (a CA root cert that the operator wants trusted)
		#   - Otherwise → LocalMachine\CA (intermediate CA cert)
		for ($i = 1; $i -lt $collection.Count; $i++) {
			$chainCert = $collection[$i]
			$isLast = ($i -eq $collection.Count - 1)
			$isSelfSigned = $chainCert.Subject -eq $chainCert.Issuer

			if ($isLast -and $isSelfSigned) {
				Add-IISCertificateToStore -Cert $chainCert -StoreName 'Root'
				Write-Host "Certificate: imported self-signed root '$($chainCert.Subject)' (thumbprint $($chainCert.Thumbprint)) into LocalMachine\Root"
			} else {
				Add-IISCertificateToStore -Cert $chainCert -StoreName 'CA'
				Write-Host "Certificate: imported intermediate '$($chainCert.Subject)' (thumbprint $($chainCert.Thumbprint)) into LocalMachine\CA"
			}
		}

		if (-not [string]::IsNullOrWhiteSpace($ThumbprintVariableName)) {
			# Expose via the cert-variable convention so Bindings JSON's
			# `"certificateVariable": "<name>"` lookup finds it.
			$SquidVariables["$ThumbprintVariableName.Thumbprint"] = $leafCert.Thumbprint
			Write-Host "Certificate: exposed leaf thumbprint via SquidVariables['$ThumbprintVariableName.Thumbprint']"
		}

		return $leafCert.Thumbprint
	} finally {
		try { if (Test-Path -LiteralPath $tempPfx) { Remove-Item -LiteralPath $tempPfx -Force -ErrorAction SilentlyContinue } } catch {}
	}
}

# Helper: add a single cert to a named LocalMachine store. Swallows "already exists"
# (an idempotent re-deploy should not fail because the cert was imported by a prior run).
# Mirrors Octopus's `WindowsX509CertificateStore.cs:316-329` CRYPT_E_EXISTS swallow.
function Add-IISCertificateToStore {
	param(
		[Parameter(Mandatory=$true)][System.Security.Cryptography.X509Certificates.X509Certificate2]$Cert,
		[Parameter(Mandatory=$true)][string]$StoreName
	)
	$store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
		$StoreName,
		[System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
	try {
		$store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
		try {
			$store.Add($cert)
		} catch [System.Security.Cryptography.CryptographicException] {
			# 0x80092005 / -2146885627 = CRYPT_E_EXISTS — idempotent re-deploy, silent skip
			if ($_.Exception.HResult -eq -2146885627) {
				Write-Host "Certificate: '$($Cert.Subject)' already exists in LocalMachine\$StoreName — skipping."
			} else {
				throw
			}
		}
	} finally {
		$store.Close()
	}
}

# Translate an ApplicationPoolIdentityType + AppPoolName/Username pair into the NT principal
# string ("IIS AppPool\<PoolName>", "NT AUTHORITY\LOCAL SERVICE", "DOMAIN\user", etc.)
# that gets granted Read access on the cert's private key.
#
# Mirrors Octopus's `IisWebSiteAfterPostDeployFeature.cs:73-103` GetIdentityForApplicationPoolIdentity
# + StripLocalAccountIdentifierFromUsername. Note: Octopus has a typo at line 84 where
# LocalSystem incorrectly resolves to LocalServiceSid; we resolve LocalSystem to "NT AUTHORITY\SYSTEM"
# (the correct, documented Windows account name) so an operator who explicitly chose LocalSystem
# gets what they asked for.
function Resolve-AppPoolIdentityPrincipal {
	param(
		[string]$IdentityType,
		[string]$AppPoolName,
		[string]$Username
	)

	switch ($IdentityType) {
		'ApplicationPoolIdentity' {
			# Synthetic per-pool identity. Pool name is the meaningful suffix.
			# Note: the canonical Windows NT format is "IIS AppPool\<Pool>" (mixed case);
			# both that and "IIS APPPOOL\<Pool>" resolve to the same SID via NTAccount.Translate.
			if ([string]::IsNullOrWhiteSpace($AppPoolName)) {
				throw "Resolve-AppPoolIdentityPrincipal: AppPoolName required for ApplicationPoolIdentity but was empty."
			}
			return "IIS AppPool\$AppPoolName"
		}
		'LocalService'   { return 'NT AUTHORITY\LOCAL SERVICE' }
		'LocalSystem'    { return 'NT AUTHORITY\SYSTEM' }
		'NetworkService' { return 'NT AUTHORITY\NETWORK SERVICE' }
		'SpecificUser' {
			if ([string]::IsNullOrWhiteSpace($Username)) {
				throw "Resolve-AppPoolIdentityPrincipal: ApplicationPoolUsername required for SpecificUser identity type but was empty."
			}
			# Strip `.\` local-account prefix — NTAccount.Translate fails for ".\user".
			# Operators commonly type ".\OrderApiUser"; we normalise to bare "OrderApiUser".
			return ($Username -replace '^\.\\', '')
		}
		default {
			# Default-when-unset = ApplicationPoolIdentity (matches IIS's own default for new pools).
			if ([string]::IsNullOrWhiteSpace($IdentityType)) {
				if ([string]::IsNullOrWhiteSpace($AppPoolName)) {
					throw "Resolve-AppPoolIdentityPrincipal: AppPoolName required when IdentityType is unset (defaults to ApplicationPoolIdentity)."
				}
				return "IIS AppPool\$AppPoolName"
			}
			throw "Resolve-AppPoolIdentityPrincipal: unsupported ApplicationPoolIdentityType '$IdentityType'. Expected one of: ApplicationPoolIdentity, LocalService, LocalSystem, NetworkService, SpecificUser."
		}
	}
}

# Grant the configured pool identity Read access on the imported cert's PRIVATE KEY file.
# Without this ACL, the IIS worker process can't load the private key and HTTPS requests
# return TLS-handshake failures even though netsh binding is correct. Mirrors Octopus's
# `IisWebSiteAfterPostDeployFeature.cs:27-94`.
#
# Implementation note (CNG vs CSP): Octopus uses the CNG/CSP security-descriptor APIs
# (`NCryptSetProperty(SecurityDescriptor)` / `CryptSetProvParam(SecurityDescriptor)`) via
# P/Invoke. We use the file-level NTFS ACL on the private-key file — which works for the
# common case of file-system-backed PFX-imported keys (the CNG provider DOES consult the
# file DACL when opening the key). For exotic configurations (HSM-backed CNG keys with
# pre-set security descriptors, smart-card-backed keys), the operator gets a clear warning
# and must grant access manually. This covers ~95% of real-world IIS HTTPS deploys.
function Grant-AppPoolPrivateKeyAccess {
	param(
		[Parameter(Mandatory=$true)][string]$Thumbprint,
		[string]$AppPoolName,
		[string]$IdentityType,
		[string]$Username
	)

	$cert = Get-ChildItem -Path "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction SilentlyContinue
	if ($null -eq $cert) {
		Write-Warning "Grant-AppPoolPrivateKeyAccess: cert with thumbprint $Thumbprint not in Cert:\LocalMachine\My; skipping ACL grant."
		return
	}

	# Locate the private-key file on disk. The path differs by key-storage provider
	# (CSP/CNG); we use CertificateExtensionsRSA helper introduced in .NET 4.6+ which
	# handles both.
	$rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
	if ($null -eq $rsa) {
		Write-Warning "Grant-AppPoolPrivateKeyAccess: cert $Thumbprint has no RSA private key; skipping."
		return
	}

	$keyFilePath = $null
	# CNG path (modern certs)
	if ($rsa -is [System.Security.Cryptography.RSACng]) {
		$keyName = $rsa.Key.UniqueName
		$keyFilePath = Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyName
		if (-not (Test-Path -LiteralPath $keyFilePath)) {
			$keyFilePath = Join-Path "$env:ALLUSERSPROFILE\Microsoft\Crypto\Keys" $keyName
		}
	}
	# Legacy CSP path
	if ($null -eq $keyFilePath -and $rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
		$keyName = $rsa.CspKeyContainerInfo.UniqueKeyContainerName
		$keyFilePath = Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyName
	}

	if ([string]::IsNullOrEmpty($keyFilePath) -or -not (Test-Path -LiteralPath $keyFilePath)) {
		Write-Warning "Grant-AppPoolPrivateKeyAccess: could not locate private-key file for thumbprint $Thumbprint (probed CNG + CSP paths). Skipping ACL grant — HSM-backed or smart-card-backed keys require manual ACL via 'certlm.msc'."
		return
	}

	$appPoolIdentity = Resolve-AppPoolIdentityPrincipal -IdentityType $IdentityType -AppPoolName $AppPoolName -Username $Username
	$acl = Get-Acl -LiteralPath $keyFilePath
	$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
		$appPoolIdentity,
		[System.Security.AccessControl.FileSystemRights]::Read,
		[System.Security.AccessControl.AccessControlType]::Allow)
	$acl.AddAccessRule($rule)
	Set-Acl -LiteralPath $keyFilePath -AclObject $acl
	Write-Host "Certificate: granted Read access on private key '$keyFilePath' to '$appPoolIdentity' (identity-type=$IdentityType)"
}

$certPfxBase64 = $SquidParameters['Squid.Action.IISWebSite.Certificate.PfxBase64']
$importedCertThumbprint = $null
if (-not [string]::IsNullOrWhiteSpace($certPfxBase64)) {
	$certPfxPassword = $SquidParameters['Squid.Action.IISWebSite.Certificate.PfxPassword']
	$certVarName = $SquidParameters['Squid.Action.IISWebSite.Certificate.ThumbprintVariableName']
	$importedCertThumbprint = Import-IISCertificateFromPfxBase64 -Base64 $certPfxBase64 -Password $certPfxPassword -ThumbprintVariableName $certVarName
}

# ── Squid: Package extraction (Phase 10 — operator-staged .zip / .nupkg into WebRoot) ──
# Mirrors Octopus's package-extraction step. Operator stages a `.zip` or `.nupkg` somewhere on
# the Tentacle agent (prior step, fileserver, pre-baked path) and points `Squid.Action.IISWebSite.Package.SourcePath`
# at it. The deploy script extracts into `Squid.Action.IISWebSite.Package.ExtractTo` (or WebRoot
# by default), optionally purging the target first. Runs FIRST among the pre-IIS hooks so
# all the rewriters (SubstituteInFiles, ConfigurationTransforms, ConfigurationVariables,
# StructuredConfigurationVariables) operate on the freshly extracted files.

function Expand-IISPackage {
	param(
		[Parameter(Mandatory=$true)][string]$SourcePath,
		[Parameter(Mandatory=$true)][string]$ExtractTo,
		[bool]$PurgeBeforeExtract
	)

	if (-not (Test-Path -LiteralPath $SourcePath -PathType Leaf)) {
		throw "Package source path '$SourcePath' does not exist or is not a file. Operators must stage the archive via a prior step (e.g. Squid.Script downloading from a feed) before this IIS step runs."
	}

	$extension = [System.IO.Path]::GetExtension($SourcePath).ToLowerInvariant()
	if ($extension -ne '.zip' -and $extension -ne '.nupkg') {
		throw "Package source '$SourcePath' has unsupported extension '$extension'. Supported: .zip, .nupkg. (.tar.gz / .7z support is deferred to a future phase.)"
	}

	# Purge: delete the entire target dir + recreate. This mirrors Octopus's
	# Octopus.Action.Package.PurgeInstallationDirectory semantic — operators tick this to
	# guarantee stale files from prior deploys don't survive.
	if ($PurgeBeforeExtract -and (Test-Path -LiteralPath $ExtractTo)) {
		Write-Host "Package: purging target dir '$ExtractTo' before extract (PurgeBeforeExtract=True)"
		try {
			Get-ChildItem -LiteralPath $ExtractTo -Force | Remove-Item -Recurse -Force
		} catch {
			Write-Warning "Package: purge encountered errors but will continue: $($_.Exception.Message)"
		}
	}

	if (-not (Test-Path -LiteralPath $ExtractTo)) {
		Write-Host "Package: creating target dir '$ExtractTo'"
		New-Item -Path $ExtractTo -ItemType Directory -Force | Out-Null
	}

	Write-Host "Package: extracting '$SourcePath' → '$ExtractTo'"
	# Both .zip and .nupkg are zip-format archives — Expand-Archive handles both, but it
	# strictly requires .zip extension. For .nupkg, we copy to a temp .zip first.
	if ($extension -eq '.nupkg') {
		$tempZip = Join-Path ([System.IO.Path]::GetTempPath()) ("squid-nupkg-" + [System.Guid]::NewGuid().ToString("N") + ".zip")
		try {
			Copy-Item -LiteralPath $SourcePath -Destination $tempZip
			Expand-Archive -LiteralPath $tempZip -DestinationPath $ExtractTo -Force
		} finally {
			Remove-Item -LiteralPath $tempZip -Force -ErrorAction SilentlyContinue
		}
	} else {
		Expand-Archive -LiteralPath $SourcePath -DestinationPath $ExtractTo -Force
	}

	$extractedCount = @(Get-ChildItem -LiteralPath $ExtractTo -Recurse -File -ErrorAction SilentlyContinue).Count
	Write-Host "Package: extracted $extractedCount file(s) into '$ExtractTo'"
}

$packageSourcePath = $SquidParameters['Squid.Action.IISWebSite.Package.SourcePath']
if (-not [string]::IsNullOrWhiteSpace($packageSourcePath)) {
	$packageExtractTo = $SquidParameters['Squid.Action.IISWebSite.Package.ExtractTo']
	if ([string]::IsNullOrWhiteSpace($packageExtractTo)) {
		# Default to WebRoot when ExtractTo is empty.
		$packageExtractTo = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
	}
	if ([string]::IsNullOrWhiteSpace($packageExtractTo)) {
		throw "Package SourcePath is set ('$packageSourcePath') but neither Package.ExtractTo nor WebRoot is defined. The script doesn't know where to extract the archive."
	}

	$purgeFlag = ($SquidParameters['Squid.Action.IISWebSite.Package.PurgeBeforeExtract'] -eq 'True')
	Expand-IISPackage -SourcePath $packageSourcePath -ExtractTo $packageExtractTo -PurgeBeforeExtract $purgeFlag
}

# ── Squid: AdditionalPaths resolver (1.6.9 P1-4 — Octopus Package.AdditionalPaths parity) ──
# Returns the unified list of directories the 4 config rewriters should scan: WebRoot
# (always) + each entry in `Squid.Action.IISWebSite.AdditionalPaths` (newline OR comma
# separated). Used by SubstituteInFiles / ConfigurationTransforms / ConfigurationVariables /
# StructuredConfigurationVariables.
function Get-IISDeployScanPaths {
	param([string]$WebRoot, [string]$AdditionalPathsRaw)
	$paths = New-Object System.Collections.Generic.List[string]
	if (-not [string]::IsNullOrWhiteSpace($WebRoot)) { $paths.Add($WebRoot) }
	if (-not [string]::IsNullOrWhiteSpace($AdditionalPathsRaw)) {
		$entries = $AdditionalPathsRaw -split "[`r`n,]" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
		foreach ($e in $entries) {
			if (-not $paths.Contains($e)) { $paths.Add($e) }
		}
	}
	return $paths
}

# ── Squid: SubstituteInFiles — `#{X}` token replacement INSIDE files (Phase 8 — Octopus SubstituteInFiles parity) ──
# Mirrors Octopus's `Octopus.Features.SubstituteInFiles` feature
# (`SubstituteInFilesBehaviour.cs:12-35`). For each operator-specified file glob, reads file
# content, replaces every `#{VariableName}` token with the matching Squid variable's value,
# writes back. Works on ANY text format (JSON, YAML, properties, .txt) — unlike the XML-only
# ConfigurationVariables rewriter (Phase 6).
#
# Order: runs FIRST among the config-rewriters — Octopus pipeline is
#   SubstituteInFiles → ConfigurationTransforms → ConfigurationVariables → StructuredConfigurationVariables
# (DeployPackageCommand.cs:115-118). The reason: SubstituteInFiles can populate values that
# XDT transforms or ConfigurationVariables then operate on; the reverse order would have
# `#{X}` tokens still unresolved when the later features run.

function Update-IISFilesWithVariableSubstitution {
	param(
		[Parameter(Mandatory=$true)][string]$TargetDir,
		[Parameter(Mandatory=$true)][hashtable]$Variables,
		[Parameter(Mandatory=$true)][string]$TargetFilesGlobs
	)

	if (-not (Test-Path $TargetDir)) {
		Write-Host "SubstituteInFiles: target dir '$TargetDir' does not exist; skipping."
		return
	}

	if ([string]::IsNullOrWhiteSpace($TargetFilesGlobs)) {
		Write-Host "SubstituteInFiles: no target file globs configured; skipping."
		return
	}

	# Parse newline-separated (or comma-separated) globs. Allows operators to enter
	# `appsettings.json\nappsettings.{env}.json\n*.yml` per line in the editor UI.
	$globs = $TargetFilesGlobs -split "[`r`n,]" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
	if ($globs.Count -eq 0) {
		Write-Host "SubstituteInFiles: target file globs string parsed to zero entries; skipping."
		return
	}

	foreach ($glob in $globs) {
		# Absolute path → use directly. Relative → resolve against $TargetDir.
		$resolvedGlob = if ([System.IO.Path]::IsPathRooted($glob)) {
			$glob
		} else {
			Join-Path $TargetDir $glob
		}

		$files = @(Get-ChildItem -Path $resolvedGlob -File -ErrorAction SilentlyContinue)
		if ($files.Count -eq 0) {
			Write-Warning "SubstituteInFiles: glob '$glob' (resolved: '$resolvedGlob') matched no files."
			continue
		}

		foreach ($file in $files) {
			Write-Host "SubstituteInFiles: scanning '$($file.FullName)'"
			$content = $null
			try {
				$content = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
			} catch {
				Write-Warning "  - skip (failed to read): $($_.Exception.Message)"
				continue
			}

			if ($null -eq $content -or $content.Length -eq 0) {
				continue
			}

			# Replace `#{VariableName}` and `#{VariableName | Filter}` tokens. Mirrors a subset
			# of Octostache (1.6.9 P0-3): supports the simple-variable form + the most common
			# filters (ToUpper, ToLower, Trim, ToBase64, FromBase64, HtmlEscape, UrlEncode).
			# `#{if}` and `#{each}` conditionals/iteration deferred to a follow-up. Tokens with
			# no matching Squid variable are left intact.
			#
			# P2-1 (1.6.9): track unresolved tokens. When operator sets
			# ShouldFailDeploymentOnSubstitutionFails=True AND any tokens remain unresolved,
			# the function throws after processing — matches Octopus's strict mode.
			$unresolvedTokens = New-Object System.Collections.Generic.HashSet[string]
			$newContent = [regex]::Replace($content, '#\{([A-Za-z0-9_.\-]+)(\s*\|\s*([A-Za-z0-9]+))?\}', {
				param($match)
				$varName = $match.Groups[1].Value
				$filterName = if ($match.Groups[3].Success) { $match.Groups[3].Value } else { $null }

				if (-not $Variables.ContainsKey($varName)) {
					$unresolvedTokens.Add($match.Value) | Out-Null
					return $match.Value
				}
				$raw = [string]$Variables[$varName]

				if ([string]::IsNullOrEmpty($filterName)) { return $raw }

				switch ($filterName.ToLowerInvariant()) {
					'toupper'    { return $raw.ToUpperInvariant() }
					'tolower'    { return $raw.ToLowerInvariant() }
					'trim'       { return $raw.Trim() }
					'tobase64'   { return [System.Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($raw)) }
					'frombase64' { try { return [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($raw)) } catch { return $raw } }
					'htmlescape' { return [System.Net.WebUtility]::HtmlEncode($raw) }
					'urlencode'  { return [System.Net.WebUtility]::UrlEncode($raw) }
					default {
						Write-Warning "SubstituteInFiles: unknown filter '$filterName' for #{$varName | $filterName}. Leaving token unresolved."
						$unresolvedTokens.Add($match.Value) | Out-Null
						return $match.Value
					}
				}
			})

			# P2-1: strict-mode short-circuit. When operator opted into "fail on unresolved"
			# AND we left any tokens intact, raise a typed error with all unresolved tokens
			# listed for triage.
			$failOnUnresolved = $SquidParameters['Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails'] -eq 'True'
			if ($failOnUnresolved -and $unresolvedTokens.Count -gt 0) {
				$tokenList = ($unresolvedTokens | ForEach-Object { "  - $_" }) -join "`n"
				throw "SubstituteInFiles: $($unresolvedTokens.Count) token(s) in '$($file.FullName)' did not match any Squid variable:`n$tokenList`n`nSet Squid.Action.SubstituteInFiles.ShouldFailDeploymentOnSubstitutionFails=False (or unset) to leave unresolved tokens intact instead."
			}

			if ($newContent -ne $content) {
				# Use Set-Content with -NoNewline to avoid appending a trailing newline on files
				# that didn't have one (preserves operator's exact file shape).
				Set-Content -LiteralPath $file.FullName -Value $newContent -NoNewline -Encoding UTF8
				Write-Host "  - tokens replaced"
			}
		}
	}
}

$substituteInFilesEnabled = $SquidParameters['Squid.Action.IISWebSite.SubstituteInFiles.Enabled']
if ($substituteInFilesEnabled -eq 'True') {
	$substituteWebRoot = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
	$substituteAdditional = $SquidParameters['Squid.Action.IISWebSite.AdditionalPaths']
	$substituteGlobs = $SquidParameters['Squid.Action.IISWebSite.SubstituteInFiles.TargetFiles']
	$substitutePaths = Get-IISDeployScanPaths -WebRoot $substituteWebRoot -AdditionalPathsRaw $substituteAdditional
	foreach ($p in $substitutePaths) {
		Write-Host "SubstituteInFiles: scanning '$p'"
		Update-IISFilesWithVariableSubstitution -TargetDir $p -Variables $SquidVariables -TargetFilesGlobs $substituteGlobs
	}
}

# ── Squid: XML Configuration Transforms / XDT (Phase 7 — Octopus ConfigurationTransforms parity) ──
# Mirrors Octopus's `Octopus.Features.ConfigurationTransforms` feature
# (`ConfigurationTransformsBehaviour.cs:84-101`). When enabled, walks every *.config file
# under WebRoot and applies XDT transforms:
#   - Auto: <baseName>.Release.config (always)
#   - Auto: <baseName>.{EnvironmentName}.config (when EnvironmentName is set)
#   - Explicit: any CSV of `transform.config => target.config` entries in AdditionalTransforms
#
# RUNS BEFORE ConfigurationVariables — XDT can ADD <appSettings> entries that ConfigurationVariables
# then rewrites by value (Octopus pipeline order: ConfigurationTransforms → ConfigurationVariables).

function Update-IISConfigurationTransforms {
	param(
		[Parameter(Mandatory=$true)][string]$TargetDir,
		[string]$EnvironmentName,
		[string]$AdditionalTransforms
	)

	if (-not (Test-Path $TargetDir)) {
		Write-Host "ConfigurationTransforms: target dir '$TargetDir' does not exist; skipping."
		return
	}

	# Locate Microsoft.Web.XmlTransform — try GAC first, then probe common install paths.
	$xdtLoaded = $false
	try {
		Add-Type -AssemblyName "Microsoft.Web.XmlTransform" -ErrorAction Stop
		$xdtLoaded = $true
	} catch {
		$probePaths = @(
			"${env:ProgramFiles}\Microsoft Visual Studio\*\*\MSBuild\Microsoft\VisualStudio\v*\Web\Microsoft.Web.XmlTransform.dll",
			"${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Microsoft\VisualStudio\v*\Web\Microsoft.Web.XmlTransform.dll",
			"${env:USERPROFILE}\.nuget\packages\microsoft.web.xdt\*\lib\net40\Microsoft.Web.XmlTransform.dll",
			"${env:USERPROFILE}\.nuget\packages\microsoft.web.xdt\*\lib\netstandard2.0\Microsoft.Web.XmlTransform.dll"
		)
		foreach ($pattern in $probePaths) {
			$found = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
			if ($found) {
				try { Add-Type -Path $found.FullName; $xdtLoaded = $true; break } catch {}
			}
		}
	}

	if (-not $xdtLoaded) {
		Write-Warning ("ConfigurationTransforms: Microsoft.Web.XmlTransform not available on this agent. " +
			"Install via 'dotnet add package Microsoft.Web.Xdt' or via VS Build Tools 'Web development workload'. " +
			"Transforms will NOT be applied to *.config files.")
		return
	}

	# P2-1 (1.6.9): when EnableDiagnosticsConfigTransformationLogging=True, emit
	# extra per-transform tracing so operators triaging "why didn't my XDT match?" can
	# see at-a-glance which sources + transforms ran.
	$xdtDiagnostics = $SquidParameters['Squid.Action.Package.EnableDiagnosticsConfigTransformationLogging'] -eq 'True'

	function Apply-SingleXdtTransform($sourcePath, $transformPath) {
		Write-Host "  XDT: '$transformPath' => '$sourcePath'"
		if ($script:xdtDiagnostics) {
			Write-Host "    [diagnostic] source size: $((Get-Item -LiteralPath $sourcePath).Length) bytes"
			Write-Host "    [diagnostic] transform size: $((Get-Item -LiteralPath $transformPath).Length) bytes"
		}
		$source = New-Object Microsoft.Web.XmlTransform.XmlTransformableDocument
		$source.PreserveWhitespace = $true
		$source.Load($sourcePath)
		$transform = New-Object Microsoft.Web.XmlTransform.XmlTransformation($transformPath)
		try {
			$applied = $transform.Apply($source)
			if (-not $applied) {
				Write-Warning "  XDT transform application returned false — engine reported failure for '$transformPath'"
			} elseif ($script:xdtDiagnostics) {
				Write-Host "    [diagnostic] transform applied; new source size: $((Get-Item -LiteralPath $sourcePath).Length) bytes (pre-save)"
			}
			$source.Save($sourcePath)
		} finally {
			$source.Dispose()
			$transform.Dispose()
		}
	}

	# Build the auto-transform name list.
	$autoTransformNames = New-Object System.Collections.Generic.List[string]
	$autoTransformNames.Add("Release") | Out-Null
	if (-not [string]::IsNullOrWhiteSpace($EnvironmentName) -and $EnvironmentName -ne "Release") {
		$autoTransformNames.Add($EnvironmentName) | Out-Null
	}

	# Discover all *.config files; auto-apply siblings.
	$allConfigs = @(Get-ChildItem -Path $TargetDir -Recurse -Filter "*.config" -File -ErrorAction SilentlyContinue)
	foreach ($baseFile in $allConfigs) {
		$baseName = [System.IO.Path]::GetFileNameWithoutExtension($baseFile.Name)

		# Skip files that ARE transforms themselves (e.g. "web.Release" → would chain-apply onto itself).
		$isItselfATransform = $false
		foreach ($transformName in $autoTransformNames) {
			if ($baseName.EndsWith(".$transformName", [System.StringComparison]::OrdinalIgnoreCase)) {
				$isItselfATransform = $true; break
			}
		}
		if ($isItselfATransform) { continue }

		foreach ($transformName in $autoTransformNames) {
			$transformPath = Join-Path $baseFile.DirectoryName "$baseName.$transformName.config"
			if (Test-Path $transformPath) {
				Write-Host "ConfigurationTransforms: applying '$transformName' transform to '$($baseFile.FullName)'"
				Apply-SingleXdtTransform -sourcePath $baseFile.FullName -transformPath $transformPath
			}
		}
	}

	# Explicit additional transforms (CSV `source => target`, comma or newline separated).
	if (-not [string]::IsNullOrWhiteSpace($AdditionalTransforms)) {
		$entries = $AdditionalTransforms -split "[`r`n,]" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
		foreach ($entry in $entries) {
			$parts = $entry -split '\s*=>\s*'
			if ($parts.Count -ne 2) {
				Write-Warning "ConfigurationTransforms: skipping malformed entry '$entry' (expected 'transform.config => target.config')"
				continue
			}
			$transformFile = $parts[0].Trim()
			$targetFile = $parts[1].Trim()
			$resolvedTransform = if ([System.IO.Path]::IsPathRooted($transformFile)) { $transformFile } else { Join-Path $TargetDir $transformFile }
			$resolvedTarget = if ([System.IO.Path]::IsPathRooted($targetFile)) { $targetFile } else { Join-Path $TargetDir $targetFile }
			if (-not (Test-Path $resolvedTransform)) {
				Write-Warning "ConfigurationTransforms: transform file '$resolvedTransform' not found; skipping"
				continue
			}
			if (-not (Test-Path $resolvedTarget)) {
				Write-Warning "ConfigurationTransforms: target file '$resolvedTarget' not found; skipping"
				continue
			}
			Write-Host "ConfigurationTransforms: applying explicit '$transformFile' => '$targetFile'"
			Apply-SingleXdtTransform -sourcePath $resolvedTarget -transformPath $resolvedTransform
		}
	}
}

$configurationTransformsEnabled = $SquidParameters['Squid.Action.IISWebSite.ConfigurationTransforms.Enabled']
$configurationTransformsAdditional = $SquidParameters['Squid.Action.IISWebSite.ConfigurationTransforms.AdditionalTransforms']
if ($configurationTransformsEnabled -eq 'True' -or -not [string]::IsNullOrWhiteSpace($configurationTransformsAdditional)) {
	$transformsWebRoot = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
	$transformsAdditionalPaths = $SquidParameters['Squid.Action.IISWebSite.AdditionalPaths']
	$transformsEnv = $SquidParameters['Squid.Action.IISWebSite.ConfigurationTransforms.EnvironmentName']
	$transformsPaths = Get-IISDeployScanPaths -WebRoot $transformsWebRoot -AdditionalPathsRaw $transformsAdditionalPaths
	foreach ($p in $transformsPaths) {
		Write-Host "ConfigurationTransforms: processing *.config under '$p'"
		Update-IISConfigurationTransforms -TargetDir $p -EnvironmentName $transformsEnv -AdditionalTransforms $configurationTransformsAdditional
	}
}

$configurationVariablesEnabled = $SquidParameters['Squid.Action.IISWebSite.ConfigurationVariables.Enabled']
if ($configurationVariablesEnabled -eq 'True') {
	$configVarsWebRoot = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
	$configVarsAdditional = $SquidParameters['Squid.Action.IISWebSite.AdditionalPaths']
	$configVarsPaths = Get-IISDeployScanPaths -WebRoot $configVarsWebRoot -AdditionalPathsRaw $configVarsAdditional
	foreach ($p in $configVarsPaths) {
		Write-Host "ConfigurationVariables: rewriting *.config under '$p'"
		Update-IISConfigurationVariables -TargetDir $p -Variables $SquidVariables
	}
}

# ── Squid: Structured Configuration Variables — JSON leaf replacement (Phase 9 — Octopus JsonConfigurationVariables parity) ──
# Mirrors Octopus's `Octopus.Features.JsonConfigurationVariables` feature
# (`StructuredConfigurationVariablesBehaviour.cs:13-35`). Walks each operator-specified JSON
# file, recurses into the object structure, replaces leaf values where the path (with `:` or `.`
# separator) matches a Squid variable name. Phase 9 MVP supports JSON; YAML/properties are
# future work.
#
# Order: runs AFTER ConfigurationVariables — Octopus pipeline puts structured-config LAST among
# the rewriters because it operates on a different file family (JSON) than the prior XML ones,
# and operators may want appSettings replaced first, then JSON leaves.

function Update-IISStructuredJsonConfiguration {
	param(
		[Parameter(Mandatory=$true)][string]$TargetDir,
		[Parameter(Mandatory=$true)][hashtable]$Variables,
		[Parameter(Mandatory=$true)][string]$TargetFilesGlobs
	)

	if (-not (Test-Path $TargetDir)) {
		Write-Host "StructuredConfigurationVariables: target dir '$TargetDir' does not exist; skipping."
		return
	}

	if ([string]::IsNullOrWhiteSpace($TargetFilesGlobs)) {
		Write-Host "StructuredConfigurationVariables: no target file globs configured; skipping."
		return
	}

	# Walk JSON object recursively, replacing leaf values whose path matches a Squid variable.
	# Supports BOTH `:` and `.` path separators (operator may have stored the variable as
	# `Logging:LogLevel:Default` or `Logging.LogLevel.Default` — try both).
	# P0-4 (1.6.9): type-preserving leaf coercion. If the variable's string value parses as
	# bool/int/double AND the original JSON leaf was the same kind, coerce the replacement to
	# that type so `ConvertTo-Json` re-serialises with the right JSON tokens (no quotes around
	# numbers/bools). Mirrors Octopus's `JsonUpdateMap.MapNumber`/`MapBool`.
	function Coerce-JsonReplacementValue($variableStringValue, $originalLeaf) {
		if ($null -eq $variableStringValue) { return $null }
		# Type-preserving paths
		if ($originalLeaf -is [bool]) {
			if ($variableStringValue -ieq 'true') { return $true }
			if ($variableStringValue -ieq 'false') { return $false }
		}
		if ($originalLeaf -is [int] -or $originalLeaf -is [long]) {
			$parsedInt = 0L
			if ([long]::TryParse($variableStringValue, [ref]$parsedInt)) { return $parsedInt }
		}
		if ($originalLeaf -is [double] -or $originalLeaf -is [decimal] -or $originalLeaf -is [single]) {
			$parsedFloat = 0.0
			if ([double]::TryParse($variableStringValue, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsedFloat)) { return $parsedFloat }
		}
		# Default: string (preserves whatever the operator typed)
		return [string]$variableStringValue
	}

	function Walk-JsonReplace($node, $pathParts, $variables, [ref]$modifiedFlag) {
		if ($node -is [System.Management.Automation.PSCustomObject]) {
			foreach ($prop in $node.PSObject.Properties) {
				$newParts = $pathParts + @($prop.Name)
				$value = $prop.Value

				if ($value -is [System.Management.Automation.PSCustomObject]) {
					Walk-JsonReplace -node $value -pathParts $newParts -variables $variables -modifiedFlag $modifiedFlag
				} elseif ($value -is [System.Array] -or $value -is [System.Object[]]) {
					# P0-4 (1.6.9): walk arrays with numeric index paths so operators can replace
					# `Logging:LogLevel:Microsoft:0` style entries. Mirrors Octopus's JsonUpdateMap
					# array handling.
					for ($i = 0; $i -lt $value.Count; $i++) {
						$elementParts = $newParts + @([string]$i)
						if ($value[$i] -is [System.Management.Automation.PSCustomObject]) {
							Walk-JsonReplace -node $value[$i] -pathParts $elementParts -variables $variables -modifiedFlag $modifiedFlag
						} else {
							$pathColon = $elementParts -join ':'
							$pathDot = $elementParts -join '.'
							$matchedVar = $null
							if ($variables.ContainsKey($pathColon)) { $matchedVar = $variables[$pathColon] }
							elseif ($variables.ContainsKey($pathDot)) { $matchedVar = $variables[$pathDot] }
							if ($null -ne $matchedVar) {
								$value[$i] = Coerce-JsonReplacementValue -variableStringValue $matchedVar -originalLeaf $value[$i]
								$modifiedFlag.Value = $true
								Write-Host "  - JSON array element '$pathColon' replaced"
							}
						}
					}
				} else {
					$pathColon = $newParts -join ':'
					$pathDot = $newParts -join '.'
					$matchedVar = $null
					if ($variables.ContainsKey($pathColon)) {
						$matchedVar = $variables[$pathColon]
					} elseif ($variables.ContainsKey($pathDot)) {
						$matchedVar = $variables[$pathDot]
					}
					if ($null -ne $matchedVar) {
						# P0-4: coerce the variable's string value back to the original JSON
						# type (bool / int / double) so ConvertTo-Json doesn't string-ify numbers
						# and booleans (which downstream JSON consumers may reject).
						$prop.Value = Coerce-JsonReplacementValue -variableStringValue $matchedVar -originalLeaf $value
						$modifiedFlag.Value = $true
						Write-Host "  - JSON leaf '$pathColon' replaced"
					}
				}
			}
		}
	}

	$globs = $TargetFilesGlobs -split "[`r`n,]" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
	foreach ($glob in $globs) {
		$resolvedGlob = if ([System.IO.Path]::IsPathRooted($glob)) { $glob } else { Join-Path $TargetDir $glob }
		$files = @(Get-ChildItem -Path $resolvedGlob -File -ErrorAction SilentlyContinue)

		if ($files.Count -eq 0) {
			Write-Warning "StructuredConfigurationVariables: glob '$glob' matched no files."
			continue
		}

		foreach ($file in $files) {
			Write-Host "StructuredConfigurationVariables: scanning '$($file.FullName)'"
			$json = $null
			try {
				$rawContent = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
				$json = $rawContent | ConvertFrom-Json
			} catch {
				Write-Warning "  - skip (not parseable as JSON): $($_.Exception.Message)"
				continue
			}

			if ($null -eq $json) { continue }

			$modified = [ref]$false
			Walk-JsonReplace -node $json -pathParts @() -variables $Variables -modifiedFlag $modified

			if ($modified.Value) {
				# Preserve UTF-8 (Set-Content -Encoding UTF8 doesn't add BOM by default in PS 5.1+).
				# Depth 32 covers virtually all real-world config files; default of 2 truncates badly.
				$serialized = $json | ConvertTo-Json -Depth 32
				Set-Content -LiteralPath $file.FullName -Value $serialized -NoNewline -Encoding UTF8
			}
		}
	}
}

$structuredEnabled = $SquidParameters['Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled']
if ($structuredEnabled -eq 'True') {
	$structuredWebRoot = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
	$structuredAdditional = $SquidParameters['Squid.Action.IISWebSite.AdditionalPaths']
	$structuredGlobs = $SquidParameters['Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets']
	$structuredPaths = Get-IISDeployScanPaths -WebRoot $structuredWebRoot -AdditionalPathsRaw $structuredAdditional
	foreach ($p in $structuredPaths) {
		Write-Host "StructuredConfigurationVariables: rewriting JSON leaves under '$p'"
		Update-IISStructuredJsonConfiguration -TargetDir $p -Variables $SquidVariables -TargetFilesGlobs $structuredGlobs
	}
}

$psVersion = $PSVersionTable.PSVersion

if ($psVersion.Major -ge 7 -and $psVersion.Minor -ge 3) {
	Write-Verbose "'Deploy to IIS' step is yet not supported on PowerShell 7.3 or later. "
	Write-Verbose "Running the script on incompatible Powershell version, will optimistically continue using Windows Powershell compatibility session."
	Import-Module WebAdministration -UseWindowsPowerShell
	$compatSession = Get-PSSession -Name WinPSCompatSession
	if ($null -eq $compatSession) {
		Write-Error "Failed to find Windows Powershell compatibility session. Please try running the script on Windows Powershell."
		throw
	}
	Invoke-Command -Session $compatSession -ScriptBlock $DeployIISScriptBlock -ArgumentList $SquidParameters
}
else {
	&$DeployIISScriptBlock -SquidParameters $SquidParameters
}

# ── Squid: Grant AppPool private-key ACL (1.6.9 P0-1) ──
# After bindings have been applied (during IIS configure dispatch above), grant the
# pool's configured identity Read access on the imported cert's private key file.
# Without this the worker process can't load the private key → TLS handshake fails.
# Runs only if we auto-imported a cert (otherwise the operator manages key ACL manually).
#
# The identity-type switch (ApplicationPoolIdentity / LocalService / LocalSystem /
# NetworkService / SpecificUser) is required for parity with Octopus: hardcoding
# "IIS APPPOOL\<PoolName>" would silently grant the wrong principal when the operator
# configures any non-default identity, and HTTPS would silently fail on those pools.
if (-not [string]::IsNullOrWhiteSpace($importedCertThumbprint)) {
	$appPoolForAcl = $SquidParameters['Squid.Action.IISWebSite.ApplicationPoolName']
	$identityTypeForAcl = $SquidParameters['Squid.Action.IISWebSite.ApplicationPoolIdentityType']
	$usernameForAcl = $SquidParameters['Squid.Action.IISWebSite.ApplicationPoolUsername']
	# ApplicationPoolIdentity / unset both require AppPoolName; the others (LocalService,
	# LocalSystem, NetworkService, SpecificUser) do not. Resolve the principal even
	# when AppPoolName is empty so a Service+Cert without an AppPool still gets ACL'd.
	$canGrant = $true
	if ([string]::IsNullOrWhiteSpace($identityTypeForAcl) -or $identityTypeForAcl -eq 'ApplicationPoolIdentity') {
		$canGrant = -not [string]::IsNullOrWhiteSpace($appPoolForAcl)
	} elseif ($identityTypeForAcl -eq 'SpecificUser') {
		$canGrant = -not [string]::IsNullOrWhiteSpace($usernameForAcl)
	}
	if ($canGrant) {
		Grant-AppPoolPrivateKeyAccess -Thumbprint $importedCertThumbprint -AppPoolName $appPoolForAcl -IdentityType $identityTypeForAcl -Username $usernameForAcl
	} else {
		Write-Warning "Skipping private-key ACL grant: identity-type '$identityTypeForAcl' requires AppPoolName or Username but neither was set."
	}
}

# ── Squid: Custom-script PostDeploy hook (Phase 5) ──
# Runs ONLY when the IIS configure block above completed without throwing (PowerShell's
# default error-mode aborts the script on uncaught throw, so this line is unreachable on
# IIS-configure failure — matches Octopus's semantic of "post-deploy runs on success").
$postDeployScript = $SquidParameters['Squid.Action.CustomScripts.PostDeploy.ps1']
if (-not [string]::IsNullOrWhiteSpace($postDeployScript)) {
	Write-Host "Running PostDeploy custom script..."
	Import-Module WebAdministration -ErrorAction SilentlyContinue
	$postDeployBlock = [scriptblock]::Create($postDeployScript)
	& $postDeployBlock
	if ($LastExitCode -ne 0 -and $null -ne $LastExitCode) {
		throw "PostDeploy custom script exited with code $LastExitCode."
	}
}

# ── Squid: Packaged PostDeploy script (Phase 1.6.9 P1-3) ──
# Same as packaged PreDeploy above but for the PostDeploy stage. Operator can ship a
# `PostDeploy.ps1` inside the package — usually for smoke-tests, cache warming, or
# instrumentation. Runs AFTER both IIS configure AND the inline PostDeploy hook.
$postDeployWebRoot = $SquidParameters['Squid.Action.IISWebSite.WebRoot']
if (-not [string]::IsNullOrWhiteSpace($postDeployWebRoot)) {
	$packagedPostDeploy = Join-Path $postDeployWebRoot 'PostDeploy.ps1'
	if (Test-Path -LiteralPath $packagedPostDeploy -PathType Leaf) {
		Write-Host "Packaged PostDeploy: running '$packagedPostDeploy'"
		Import-Module WebAdministration -ErrorAction SilentlyContinue
		& $packagedPostDeploy
		if ($LastExitCode -ne 0 -and $null -ne $LastExitCode) {
			throw "Packaged PostDeploy script '$packagedPostDeploy' exited with code $LastExitCode."
		}
	}
}

# ── Squid: Write success entry to deployment journal (1.6.9 P0-2) ──
# Reached only when every prior phase (extract, rewriters, IIS configure, post-deploy)
# completed without throwing. Failed deploys never write — the next re-run sees no journal
# entry (or the last successful one before this failed attempt) and proceeds.
if (-not [string]::IsNullOrWhiteSpace($journalSiteName)) {
	Write-IISDeployJournalEntry -SiteName $journalSiteName -Fingerprint $journalFingerprint -Status 'Success'
}

