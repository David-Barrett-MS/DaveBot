[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$DnsName = 'brain.daves.tips',
    [string]$IpAddress = '192.168.0.113',
    [string]$SettingsPath = (Join-Path $PSScriptRoot 'appsettings.json')
)

$principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
{
    throw 'Run this script from an elevated PowerShell session on CrankyServer.'
}

$parsedIpAddress = $null
if (-not [Net.IPAddress]::TryParse($IpAddress, [ref]$parsedIpAddress))
{
    throw 'IpAddress must be a valid IP address.'
}

if (-not (Test-Path -LiteralPath $SettingsPath))
{
    throw "The DaveBrain settings file was not found at '$SettingsPath'."
}

function Find-UsableCertificate([string]$subject)
{
    Get-ChildItem -Path Cert:\LocalMachine\My |
        Where-Object {
            $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Add-OrSetProperty($object, [string]$name, $value)
{
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property)
    {
        $object | Add-Member -NotePropertyName $name -NotePropertyValue $value
    }
    else
    {
        $property.Value = $value
    }
}

function Get-OrAddObjectProperty($object, [string]$name)
{
    $property = $object.PSObject.Properties[$name]
    if ($null -eq $property -or $null -eq $property.Value)
    {
        $value = [pscustomobject]@{}
        Add-OrSetProperty $object $name $value
        return $value
    }

    return $property.Value
}

$tlsSubject = "CN=$DnsName"
$tlsCertificate = Find-UsableCertificate $tlsSubject
if ($null -eq $tlsCertificate -and $PSCmdlet.ShouldProcess(
    'Cert:\LocalMachine\My',
    "Create a non-exportable self-signed TLS certificate for $DnsName and $IpAddress"))
{
    $tlsCertificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $tlsSubject `
        -FriendlyName 'DaveBrain HTTPS' `
        -CertStoreLocation Cert:\LocalMachine\My `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @(
            '2.5.29.19={critical}{text}CA=false',
            "2.5.29.17={text}dns=$DnsName&ipaddress=$IpAddress",
            '2.5.29.37={text}1.3.6.1.5.5.7.3.1')
}

if ($null -ne $tlsCertificate)
{
    $trustedRootStore = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::Root,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $trustedRootStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try
    {
        $trustedCertificate = $trustedRootStore.Certificates.Find(
            [Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,
            $tlsCertificate.Thumbprint,
            $false)
        if ($trustedCertificate.Count -eq 0 -and $PSCmdlet.ShouldProcess(
            'Cert:\LocalMachine\Root',
            "Trust the public self-signed TLS certificate for $DnsName"))
        {
            $trustedRootStore.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $tlsCertificate.Export(
                    [Security.Cryptography.X509Certificates.X509ContentType]::Cert)))
        }
    }
    finally
    {
        $trustedRootStore.Dispose()
    }
}

$signingSubject = 'CN=DaveBot OAuth Token Signing'
$signingCertificate = Find-UsableCertificate $signingSubject
if ($null -eq $signingCertificate -and $PSCmdlet.ShouldProcess(
    'Cert:\LocalMachine\My',
    'Create a non-exportable DaveIdentity signing and encryption certificate'))
{
    $signingCertificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $signingSubject `
        -FriendlyName 'DaveBot OAuth Token Signing' `
        -CertStoreLocation Cert:\LocalMachine\My `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -KeyExportPolicy NonExportable `
        -KeyUsage DigitalSignature, KeyEncipherment `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension '2.5.29.19={critical}{text}CA=false'
}

$earAuthoritySubject = 'CN=DaveBot Ear Client CA'
$earAuthorityCertificate = Find-UsableCertificate $earAuthoritySubject
if ($null -eq $earAuthorityCertificate -and $PSCmdlet.ShouldProcess(
    'Cert:\LocalMachine\My',
    'Create a non-exportable DaveBot Ear client-certificate authority'))
{
    $earAuthorityCertificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $earAuthoritySubject `
        -FriendlyName 'DaveBot Ear Client CA' `
        -CertStoreLocation Cert:\LocalMachine\My `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -KeyExportPolicy NonExportable `
        -KeyUsage CertSign, CRLSign `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension '2.5.29.19={critical}{text}CA=true&pathlength=0'
}

if ($null -eq $tlsCertificate -or
    $null -eq $signingCertificate -or
    $null -eq $earAuthorityCertificate)
{
    return
}

$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$identity = Get-OrAddObjectProperty $settings 'DaveIdentity'
Add-OrSetProperty $identity 'TokenSigningCertificateThumbprint' $signingCertificate.Thumbprint
Add-OrSetProperty $identity 'EarClientCertificateAuthorityThumbprint' $earAuthorityCertificate.Thumbprint
$daveBot = Get-OrAddObjectProperty $settings 'DaveBot'
Add-OrSetProperty $daveBot 'EarMtlsPort' 8443
$kestrel = Get-OrAddObjectProperty $settings 'Kestrel'
$endpoints = Get-OrAddObjectProperty $kestrel 'Endpoints'
$uiEndpoint = Get-OrAddObjectProperty $endpoints 'Https'
Add-OrSetProperty $uiEndpoint 'Url' "https://$($IpAddress):443"
$uiCertificate = Get-OrAddObjectProperty $uiEndpoint 'Certificate'
Add-OrSetProperty $uiCertificate 'Subject' $DnsName
Add-OrSetProperty $uiCertificate 'Store' 'My'
Add-OrSetProperty $uiCertificate 'Location' 'LocalMachine'
Add-OrSetProperty $uiCertificate 'AllowInvalid' $false
$earEndpoint = Get-OrAddObjectProperty $endpoints 'DaveEarMtls'
Add-OrSetProperty $earEndpoint 'Url' "https://$($IpAddress):8443"
Add-OrSetProperty $earEndpoint 'ClientCertificateMode' 'RequireCertificate'
$earCertificate = Get-OrAddObjectProperty $earEndpoint 'Certificate'
Add-OrSetProperty $earCertificate 'Subject' $DnsName
Add-OrSetProperty $earCertificate 'Store' 'My'
Add-OrSetProperty $earCertificate 'Location' 'LocalMachine'
Add-OrSetProperty $earCertificate 'AllowInvalid' $false
$temporaryPath = "$SettingsPath.$([Guid]::NewGuid().ToString('N')).tmp"
[IO.File]::WriteAllText(
    $temporaryPath,
    ($settings | ConvertTo-Json -Depth 20),
    [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporaryPath -Destination $SettingsPath -Force

[pscustomobject]@{
    TlsCertificateThumbprint = $tlsCertificate.Thumbprint
    TokenSigningCertificateThumbprint = $signingCertificate.Thumbprint
    EarClientCertificateAuthorityThumbprint = $earAuthorityCertificate.Thumbprint
    Store = 'Cert:\LocalMachine\My'
}
