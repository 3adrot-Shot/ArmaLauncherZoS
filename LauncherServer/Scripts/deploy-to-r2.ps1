# deploy-to-r2.ps1
# Deploy chunked model to Cloudflare R2

param(
    [Parameter(Mandatory=$true)]
    [string]$ChunksDirectory,
    
    [Parameter(Mandatory=$true)]
    [string]$ManifestFile,
    
    [Parameter(Mandatory=$true)]
    [string]$BucketName,
    
    [Parameter(Mandatory=$true)]
    [string]$AccountId,
    
    [Parameter(Mandatory=$false)]
    [int]$ConcurrentUploads = 32
)

$ErrorActionPreference = "Stop"

Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host "?         Cloudflare R2 Model Deployment Script                ?" -ForegroundColor Cyan
Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host ""

# Verify wrangler is installed
if (-not (Get-Command "wrangler" -ErrorAction SilentlyContinue)) {
    Write-Error "wrangler CLI not found. Install with: npm install -g wrangler"
    exit 1
}

# Get all chunk files
$chunks = Get-ChildItem -Path $ChunksDirectory -Recurse -File
$totalChunks = $chunks.Count
$uploaded = 0
$skipped = 0

Write-Host "Found $totalChunks chunks to upload" -ForegroundColor Green
Write-Host ""

# Upload chunks in parallel batches
$chunks | ForEach-Object -ThrottleLimit $ConcurrentUploads -Parallel {
    $chunk = $_
    $relativePath = $chunk.FullName.Substring($using:ChunksDirectory.Length + 1).Replace("\", "/")
    $key = "chunks/$relativePath"
    
    # Check if already exists (wrangler r2 object head)
    $exists = $false
    try {
        $null = wrangler r2 object head $using:BucketName $key 2>$null
        $exists = $true
    } catch {}
    
    if (-not $exists) {
        # Upload with immutable cache headers
        wrangler r2 object put $using:BucketName $key `
            --file $chunk.FullName `
            --content-type "application/octet-stream" `
            --cache-control "public, immutable, max-age=31536000"
        
        Write-Host "? Uploaded: $key" -ForegroundColor Green
    } else {
        Write-Host "? Skipped (exists): $key" -ForegroundColor Yellow
    }
}

# Upload manifest
$manifestKey = "manifests/" + (Split-Path $ManifestFile -Leaf)
wrangler r2 object put $BucketName $manifestKey `
    --file $ManifestFile `
    --content-type "application/x-msgpack" `
    --cache-control "public, max-age=300"

Write-Host ""
Write-Host "? Manifest uploaded: $manifestKey" -ForegroundColor Green

# Upload public key if exists
$publicKeyPath = Join-Path (Split-Path $ManifestFile -Parent) "signing.pub"
if (Test-Path $publicKeyPath) {
    wrangler r2 object put $BucketName "keys/signing.pub" `
        --file $publicKeyPath `
        --content-type "application/octet-stream" `
        --cache-control "public, max-age=86400"
    
    Write-Host "? Public key uploaded" -ForegroundColor Green
}

Write-Host ""
Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host "?                    Deployment Complete                        ?" -ForegroundColor Cyan
Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host ""
Write-Host "R2 Bucket: $BucketName" -ForegroundColor White
Write-Host "Public URL: https://$AccountId.r2.cloudflarestorage.com/$BucketName" -ForegroundColor White
