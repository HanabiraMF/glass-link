param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

function Show-Message([string]$text, [string]$title = 'GlassLink') {
    [System.Windows.Forms.MessageBox]::Show($text, $title, 'OK', 'Information') | Out-Null
}

try {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Show-Message '只能发送文件。'
        exit 1
    }

    $base = 'http://localhost:5180'
    $status = Invoke-RestMethod -Uri "$base/api/status" -TimeoutSec 3
    if (-not $status.streaming) {
        Show-Message '当前没有开启串流，文件未发送。'
        exit 1
    }

    $name = [IO.Path]::GetFileName($Path)
    $headers = @{ 'X-File-Name' = [Uri]::EscapeDataString($name) }
    $result = Invoke-RestMethod -Uri "$base/api/files" -Method Post -InFile $Path `
        -ContentType 'application/octet-stream' -Headers $headers -TimeoutSec 120
    Show-Message "已发送：$name`n手机端会显示下载入口。"
    exit 0
}
catch {
    Show-Message ("发送失败：" + $_.Exception.Message)
    exit 1
}
