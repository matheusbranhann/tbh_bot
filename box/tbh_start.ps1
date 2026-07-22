# tbh_start.ps1 -- clone-start: Steam + login (Family "+") + baixa painel/tbh_core + autopilot + supervisor.
$ErrorActionPreference = "SilentlyContinue"
$ProgressPreference = "SilentlyContinue"
$DL     = "C:\tbh_auto"
$REPO   = "matheusbranhann/taskbarhero-bot"
$GAMEID = "3678970"
$LOG    = "$DL\start.log"
function Log($m) { ("[{0}] {1}" -f (Get-Date -Format HH:mm:ss), $m) | Tee-Object -FilePath $LOG -Append | Out-Null; Write-Output $m }
New-Item -ItemType Directory -Force -Path $DL | Out-Null

function GameUp { [bool](Get-Process TaskBarHero -ErrorAction SilentlyContinue) }

# 1) SEMPRE abre a Steam DESTA box. NAO gatear por Get-Process: dentro do Sandboxie ele
#    enxerga a Steam do HOST (e de outras boxes) -> gatear pularia abrir a Steam desta box.
#    O steam.exe lancado aqui e confinado nesta box (instancia propria, mutex isolado);
#    com Family OFF + contas limpas, abre direto na TELA DE LOGIN. Se ja houver Steam nesta
#    box, o 2o steam.exe so repassa e sai (sem 2a janela) -> chamar sempre e seguro.
Log "abrindo a Steam desta box"
# -silent: sem a janela da loja (a tela de LOGIN continua aparecendo quando a conta
# nao esta logada); -cef-*: desligam GPU nos steamwebhelper -> menos RAM por box.
Start-Process "C:\Program Files (x86)\Steam\steam.exe" -ArgumentList "-silent","-cef-disable-gpu","-cef-disable-gpu-compositing"
Start-Sleep -Seconds 22

# 2) lanca o jogo e ESPERA o usuario logar na tela de login (sem picker/clique) -> o jogo sobe.
#    Se JA estiver logado, sobe direto. Ate ~7min p/ o usuario digitar as credenciais.
Log "lancando o jogo (aguardando login do usuario se preciso)"
Start-Process "steam://rungameid/$GAMEID"
for ($i=0; $i -lt 210; $i++) {
  if (GameUp) { Log "jogo SUBIU"; break }
  Start-Sleep -Seconds 2
  if ($i -eq 45 -or $i -eq 90 -or $i -eq 150) { Start-Process "steam://rungameid/$GAMEID" }   # re-tenta apos o login
}
if (-not (GameUp)) { Log "jogo nao subiu (login pendente) -- supervisor cuida" }

# 3) painel + tbh_core + offsets -> DELEGADO ao tbh_sync.ps1, que nao depende do jogo aberto.
#    (o bloco antigo baixava o tbh_core.py da RAIZ do repo -- caminho que morreu na v4.0, quando o
#    projeto Python foi pra python_old_project/ -- e "materializava" offsets rodando o painel com o
#    jogo carregado. Agora os offsets vem do feed pelo hash do GameAssembly.dll no disco.)
# NOTA: caminho com BARRA NORMAL de proposito. Aqui ja houve um TAB literal onde devia estar a
# barra invertida seguida de "t" (o arquivo foi gravado por uma camada que interpretou o escape),
# entao o -File apontava pra um caminho inexistente -- e o Out-Null engolia o erro. Resultado: o
# sync NUNCA rodou na subida da box, e as instancias foram acumulando core/offsets velhos.
$sync = "C:/tbh_auto/tbh_sync.ps1"
if (-not (Test-Path $sync)) {
  Log "ERRO: $sync nao existe -- a box vai subir com o que ja tinha"
} else {
  $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $sync 2>&1
  if ($LASTEXITCODE -ne 0) { Log "AVISO: tbh_sync saiu com codigo $LASTEXITCODE" }
  Log "sync (painel/core/offsets) executado"
}
if (-not (Test-Path "$DL\profiles.json")) { Copy-Item "$env:USERPROFILE\Desktop\profiles.json" "$DL\profiles.json" -Force; Log "profiles.json copiado do Desktop" }

# 4) mata autopilot/supervisor antigos (PID file; WMI bloqueado) e sobe SO o SUPERVISOR
#    (o supervisor e o DONO UNICO do autopilot -> evita o race de dois autopilots brigando pelo dispatcher)
foreach ($n in @("autopilot","supervisor")) {
  if (Test-Path "$DL\$n.pid") { Stop-Process -Id (Get-Content "$DL\$n.pid") -Force -ErrorAction SilentlyContinue }
}
Log "subindo o supervisor (ele gerencia o autopilot)"
Start-Process python -ArgumentList "$DL\tbh_supervisor.py" -WorkingDirectory $DL -WindowStyle Hidden
Log "startup completo"
