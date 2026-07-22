# tbh_sync.ps1 -- coloca a box em dia SEM abrir o jogo: painel + tbh_core + offsets do build instalado.
# Extraido do tbh_start.ps1 (passo 3) e corrigido:
#   - tbh_core.py mudou de lugar no repo na v4.0 (raiz -> python_old_project/); a URL antiga da 404
#     e o catch engolia o erro, entao a box ficava com o core velho pra sempre.
#   - offsets NAO dependem mais do jogo aberto: vem do FEED (offsets/offsets_<hash>.json no repo),
#     com o hash calculado do GameAssembly.dll NO DISCO. Antes exigia rodar o painel com o jogo
#     carregado pra "materializar", o que impedia preparar uma box sem ligar o jogo.
$ErrorActionPreference = "SilentlyContinue"
$ProgressPreference    = "SilentlyContinue"
$DL   = "C:\tbh_auto"
$REPO = "matheusbranhann/taskbarhero-bot"
$UA   = @{ "User-Agent" = "tbh" }
$LOG  = "$DL\sync.log"
function Log($m) { $l = "[{0}] {1}" -f (Get-Date -Format HH:mm:ss), $m; $l | Add-Content $LOG; Write-Output $l }
New-Item -ItemType Directory -Force -Path $DL, "$DL\cache" | Out-Null

# ---------- 1) acha o GameAssembly.dll (sem precisar do jogo rodando) ----------
$cands = @(
  "C:\Program Files (x86)\Steam\steamapps\common\TaskbarHero\GameAssembly.dll",
  "C:\Program Files\Steam\steamapps\common\TaskbarHero\GameAssembly.dll",
  "D:\SteamLibrary\steamapps\common\TaskbarHero\GameAssembly.dll"
)
$ga = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $ga) { Log "GameAssembly.dll nao encontrado - sem offsets"; }

# ---------- 2) hash do build: md5 dos 2.000.000 PRIMEIROS bytes (mesma regra do painel) ----------
$hash = $null
if ($ga) {
  $fs = [IO.File]::OpenRead($ga)
  $buf = New-Object byte[] 2000000
  $n = $fs.Read($buf, 0, 2000000); $fs.Close()
  $md5 = [Security.Cryptography.MD5]::Create().ComputeHash($buf, 0, $n)
  $hash = (([BitConverter]::ToString($md5) -replace "-", "").ToLower()).Substring(0, 12)
  Log "build do jogo instalado: $hash"
}

# ---------- 3) painel da release mais nova (so quando a TAG muda: e um zip de ~70MB) ----------
try {
  $r = Invoke-RestMethod "https://api.github.com/repos/$REPO/releases/latest" -Headers $UA
  $tag = $r.tag_name
  $asset = $r.assets | Where-Object { $_.name -like "*.zip" } | Select-Object -First 1
  $have = if (Test-Path "$DL\ver.txt") { Get-Content "$DL\ver.txt" } else { "" }
  if ($have -ne $tag) {
    Log "baixando painel $tag (tinha: '$have')"
    Invoke-WebRequest $asset.browser_download_url -OutFile "$DL\panel.zip" -Headers $UA
    Expand-Archive "$DL\panel.zip" $DL -Force
    Set-Content "$DL\ver.txt" $tag
  } else { Log "painel ja esta na $tag" }
} catch { Log "download do release falhou: $($_.Exception.Message)" }

# ---------- 3b) scripts python: SEMPRE do main, NUNCA presos a tag ----------
# Ficavam dentro do "if tag mudou" do passo 3. Como correcao de tbh_core.py entra no main sem cortar
# release, a box na mesma tag nunca pegava o conserto -- foi assim que tres instancias ficaram com um
# core velho (sem o fix do godmode) por dias. Sao arquivos pequenos: baixar todo sync sai de graca.
foreach ($f in @(
  @{ url = "https://raw.githubusercontent.com/$REPO/main/python_old_project/tbh_core.py"; dst = "$DL\tbh_core.py";   min = 100000 },
  @{ url = "https://raw.githubusercontent.com/$REPO/main/box/tbh_unlock.py";              dst = "$DL\tbh_unlock.py"; min = 500    }
)) {
  try {
    Invoke-WebRequest $f.url -OutFile "$($f.dst).new" -Headers $UA
    if ((Get-Item "$($f.dst).new").Length -ge $f.min) {          # trunca/404 nao substitui o que funciona
      Move-Item "$($f.dst).new" $f.dst -Force
      Log ("{0} atualizado ({1} bytes)" -f (Split-Path $f.dst -Leaf), (Get-Item $f.dst).Length)
    } else {
      Remove-Item "$($f.dst).new" -Force -EA SilentlyContinue
      Log ("AVISO: {0} veio pequeno demais - mantive o que ja tinha" -f (Split-Path $f.dst -Leaf))
    }
  } catch { Log ("AVISO: nao baixei {0}: {1}" -f (Split-Path $f.dst -Leaf), $_.Exception.Message) }
}
Remove-Item "$DL\__pycache__\tbh_core*.pyc" -Force -ErrorAction SilentlyContinue

# ---------- 4) offsets do build instalado, direto do FEED (nao precisa do jogo aberto) ----------
# O cache so vale se for do EXTRATOR novo (_ver >= 7) e tiver o dispatcher (upd/llx). Sem upd/llx o
# painel loga "dispatcher: offset upd/llx missing" e auto-box/auto-stash/auto-boss ficam mortos --
# foi exatamente esse o estado de uma instancia que ficou com offsets do build anterior.
function OffsetsOk($p) {
  if (-not (Test-Path $p)) { return $false }
  try { $j = Get-Content $p -Raw | ConvertFrom-Json } catch { return $false }
  return ($j._ver -ge 7 -and $j.gra -and $j.upd -and $j.llx)
}
if ($hash) {
  $dest = "$DL\cache\offsets_$hash.json"
  if (OffsetsOk $dest) {
    Log "offsets do build $hash ja estao no cache e sao validos"
  } else {
    if (Test-Path $dest) { Log "offsets do build $hash no cache sao invalidos/antigos - rebaixando" }
    try {
      Invoke-WebRequest "https://raw.githubusercontent.com/$REPO/main/offsets/offsets_$hash.json" `
        -OutFile "$dest.new" -Headers $UA
      if (OffsetsOk "$dest.new") {
        Move-Item "$dest.new" $dest -Force
        Log "offsets do build $hash baixados do feed"
      } else {
        Remove-Item "$dest.new" -Force -EA SilentlyContinue
        Log "AVISO: json do feed invalido/antigo - descartado (mantive o cache anterior)"
      }
    } catch {
      Log "AVISO: build $hash ainda nao esta no feed (offsets/) - o painel roda so com AOB ate publicar"
    }
  }
}

# ---------- 5) deps do python ----------
python -m pip install --quiet --disable-pip-version-check pymem capstone 2>&1 | Out-Null
Log "sync completo"
