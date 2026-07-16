$ErrorActionPreference = 'Stop'

Write-Host 'Checking .NET...'
dotnet --info

Write-Host 'Checking FFmpeg...'
ffmpeg -version | Select-Object -First 1

Write-Host 'Checking FFprobe...'
ffprobe -version | Select-Object -First 1

Write-Host 'All command-line prerequisites are available.' -ForegroundColor Green

