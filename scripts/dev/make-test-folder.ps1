# Cria uma pasta de teste com muitos arquivos pequenos (para medir pastas grandes com bench.ps1).
#
#   powershell -NoProfile -File scripts\dev\make-test-folder.ps1 -Path C:\DoraDevTest\plana-100k -Count 100000
#
# Leva cerca de 90 s para 100 mil arquivos. Para apagar depois: Remove-Item -LiteralPath <pasta> -Recurse -Force
# (apague a pasta de teste, nunca a raiz de uma unidade). Este arquivo e ASCII de proposito (PowerShell 5.1).
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$Count = 100000
)

$ErrorActionPreference = 'Stop'

if (-not ('DoraTestFolder' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.IO;
public static class DoraTestFolder {
  public static void Flat(string dir, int count) {
    Directory.CreateDirectory(dir);
    var one = new byte[] { 65 };
    for (int i = 0; i < count; i++) File.WriteAllBytes(Path.Combine(dir, "arquivo_" + i.ToString("D6") + ".txt"), one);
  }
}
'@
}

$watch = [Diagnostics.Stopwatch]::StartNew()
[DoraTestFolder]::Flat($Path, $Count)
"criados $Count arquivos em $Path ({0:N0} s)" -f $watch.Elapsed.TotalSeconds
