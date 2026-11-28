# Source id of swh2coop.dll (build.cmd compiles it in, NetSession.DllId reads it): SHA-256 of the sources with line
# ends as LF, so the same sources give the same id on every PC (git may check them out with CRLF or LF).
$sha = [Security.Cryptography.SHA256]::Create()
$text = ''
foreach ($f in 'swh2coop.c', 'netplay.c', 'nettcp.c', 'steamnet.c', 'exemap.c', 'netplay.h', 'steamnet.h', 'map_gog.h') {
    $text += $f + "`n" + [IO.File]::ReadAllText((Join-Path $PSScriptRoot $f)).Replace("`r", '')
}
-join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('X2') })
