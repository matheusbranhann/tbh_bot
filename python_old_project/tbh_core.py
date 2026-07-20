#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TBH CORE — motor unico do painel TaskBarHero.
- Attach no jogo (pymem), AOB scan self-relocating.
- AUTO-OFFSETS: se o jogo atualizar (GameAssembly.dll mudou), re-dumpa com Il2CppDumper
  sozinho e extrai os novos RVAs (gra/hitkill, nq<bau>/inventario, ACTk). Cacheia por hash.
- Cheats: ACTk bypass, God Mode, Hitkill, 25 Stats (chain Pstat), Stage editor.
- Inventario: le TODOS os itens (inv+baus) de PlayerSaveData.itemSaveDatas.
- Precos: indice do Steam Market (market_prices.json) com lookup por nome+grade.
Nada aqui abre GUI; e importado por tbh_panel.py.
"""
import os, sys, struct, subprocess, hashlib, json, re, time, ctypes, collections, difflib, threading, bisect
from ctypes import wintypes

# ===================== AUTO-UPDATE (via GitHub Releases) =====================
VERSION="3.5"                                   # BUMPAR a cada release; o exe compara com a tag do 'releases/latest'
GITHUB_REPO="matheusbranhann/taskbarhero-bot"

def _ver_tuple(s):
    """'v3.1'/'3.10' -> (3,1)/(3,10) pra comparar versao ordinalmente."""
    out=[]
    for p in str(s or "").lstrip("vV").strip().split("."):
        d="".join(ch for ch in p if ch.isdigit())
        out.append(int(d) if d else 0)
    return tuple(out) or (0,)

def check_update(timeout=8):
    """Consulta releases/latest do GitHub. Retorna {tag,url,size,notes} se houver versao MAIOR que VERSION,
    senao None. So urllib (stdlib, ja usado pra precos Steam -> funciona no exe). Silencioso em falha de rede."""
    try:
        import urllib.request
        req=urllib.request.Request("https://api.github.com/repos/%s/releases/latest"%GITHUB_REPO,
                                   headers={"User-Agent":"tbh_bot-updater","Accept":"application/vnd.github+json"})
        with urllib.request.urlopen(req,timeout=timeout) as r:
            d=json.load(r)
        tag=d.get("tag_name","")
        if _ver_tuple(tag)>_ver_tuple(VERSION):
            asset=next((a for a in d.get("assets",[]) if str(a.get("name","")).lower().endswith(".zip")),None)
            if asset and asset.get("browser_download_url"):
                return {"tag":tag,"url":asset["browser_download_url"],"size":asset.get("size",0),"notes":d.get("body","")}
    except Exception:
        pass
    return None

def download_update(url, progress=None, timeout=60):
    """Baixa o zip do release e extrai TBH_Panel.exe pra <dir do exe>/TBH_Panel.new.exe.
    progress(frac 0..1) opcional. Retorna (newexe, exe, exedir). So funciona no exe congelado."""
    import urllib.request, zipfile, io
    exe=sys.executable; exedir=os.path.dirname(exe)
    req=urllib.request.Request(url,headers={"User-Agent":"tbh_bot-updater"})
    buf=io.BytesIO()
    with urllib.request.urlopen(req,timeout=timeout) as r:
        total=int(r.headers.get("Content-Length") or 0); read=0
        while True:
            chunk=r.read(65536)
            if not chunk: break
            buf.write(chunk); read+=len(chunk)
            if progress and total: progress(min(read/total,1.0))
    with zipfile.ZipFile(io.BytesIO(buf.getvalue())) as z:
        name=next(n for n in z.namelist() if n.lower().endswith("tbh_panel.exe"))
        data=z.read(name)
    if len(data)<10_000_000:                    # sanidade: o exe real e ~44MB; menor que isso = download torto
        raise RuntimeError("exe baixado pequeno demais (%d bytes)"%len(data))
    newexe=os.path.join(exedir,"TBH_Panel.new.exe")
    with open(newexe,"wb") as f: f.write(data)
    return newexe, exe, exedir

_UPDATER_BAT=(
    "@echo off\r\n"
    "setlocal\r\n"
    'set "EXE=%~1"\r\n'
    'set "NEW=%~2"\r\n'
    "set PID=%~3\r\n"
    ":wait\r\n"
    'tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul\r\n'
    "if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto wait )\r\n"
    ":movetry\r\n"
    'move /y "%NEW%" "%EXE%" >nul 2>&1\r\n'      # espera o exe destravar (processo saiu); retenta se ainda preso
    "if errorlevel 1 ( timeout /t 1 /nobreak >nul & goto movetry )\r\n"
    'start "" "%EXE%"\r\n'                       # reabre o painel NOVO
    'del "%~f0"\r\n')

def launch_updater(newexe, exe, exedir):
    """Escreve o .bat updater e o lanca DESACOPLADO. Ele espera este processo (PID) sair, troca o exe e reabre.
    O chamador deve encerrar o painel logo apos (senao o move fica retentando ate ele fechar)."""
    bat=os.path.join(exedir,"_tbh_update.bat")
    with open(bat,"w",encoding="ascii") as f: f.write(_UPDATER_BAT)
    DETACHED=0x00000008|0x00000200              # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    subprocess.Popen(["cmd","/c",bat,exe,newexe,str(os.getpid())],cwd=exedir,
                     creationflags=DETACHED,close_fds=True,
                     stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)

class MBI(ctypes.Structure):
    _fields_=[("BaseAddress",ctypes.c_void_p),("AllocationBase",ctypes.c_void_p),("AllocationProtect",ctypes.c_uint32),
              ("__a",ctypes.c_uint32),("RegionSize",ctypes.c_size_t),("State",ctypes.c_uint32),("Protect",ctypes.c_uint32),("Type",ctypes.c_uint32)]
_VQ=ctypes.windll.kernel32.VirtualQueryEx; _VQ.restype=ctypes.c_size_t

# HERE = pasta do .exe (quando empacotado) ou do .py. O .exe deve ficar na pasta do jogo.
HERE=os.path.dirname(sys.executable) if getattr(sys,"frozen",False) else os.path.dirname(os.path.abspath(__file__))
def P(*a): return os.path.join(HERE,*a)

def _ensure_assets():
    # No .exe onefile, extrai os dados empacotados (_MEIPASS) pra pasta do exe se faltarem.
    if not getattr(sys,"frozen",False): return
    src=getattr(sys,"_MEIPASS",None)
    if not src: return
    import shutil
    def _broken(path):
        # market_prices.json quebrado = existe mas vazio/tiny (rate-limit apagou)
        if os.path.basename(path)!="market_prices.json": return False
        try: return int(json.load(open(path,encoding="utf-8")).get("count",0))<50
        except Exception: return True
    for rel in ["item_prices.json","market_prices.json",
                os.path.join("tools","Il2CppDumper","Il2CppDumper.exe"),
                os.path.join("tools","Il2CppDumper","config.json")]:
        s=os.path.join(src,rel); dst=os.path.join(HERE,rel)
        if os.path.exists(s) and (not os.path.exists(dst) or _broken(dst)):
            try:
                os.makedirs(os.path.dirname(dst),exist_ok=True); shutil.copy2(s,dst)
            except Exception: pass
_ensure_assets()
def _steam_libraries():
    """Todas as bibliotecas Steam da maquina (via registro + libraryfolders.vdf) — NAO hardcode.
    Assim o jogo e achado em qualquer PC, esteja em C:, D:, num SSD externo, etc."""
    libs=[]
    try:
        import winreg
        for root,key in ((winreg.HKEY_CURRENT_USER,r"Software\Valve\Steam"),
                         (winreg.HKEY_LOCAL_MACHINE,r"SOFTWARE\WOW6432Node\Valve\Steam")):
            try:
                with winreg.OpenKey(root,key) as k:
                    sp=winreg.QueryValueEx(k,"SteamPath")[0]
                    if sp: libs.append(os.path.normpath(sp))
            except Exception: pass
    except Exception: pass
    for base in list(libs):
        vdf=os.path.join(base,"steamapps","libraryfolders.vdf")
        try:
            txt=open(vdf,encoding="utf-8",errors="ignore").read()
            for m in re.finditer(r'"path"\s*"([^"]+)"',txt):
                libs.append(os.path.normpath(m.group(1).replace("\\\\","\\")))
        except Exception: pass
    return libs

def _dotnet6_ok():
    """.NET 6+ runtime disponivel? (o Il2CppDumper e .NET 6 self-contained-host). Sem ele nao adianta
    tentar dumpar — a maioria das maquinas so tem o .NET Framework 4.8, que NAO serve."""
    try:
        r=subprocess.run(["dotnet","--list-runtimes"],capture_output=True,text=True,timeout=8,
                         creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
        for ln in (r.stdout or "").splitlines():
            m=re.match(r'Microsoft\.NETCore\.App (\d+)\.',ln)
            if m and int(m.group(1))>=6: return True
    except Exception: pass
    # fallback: procura o host do runtime instalado
    for pf in (os.environ.get("ProgramFiles",r"C:\Program Files"),):
        d=os.path.join(pf,"dotnet","shared","Microsoft.NETCore.App")
        try:
            if any(v.split(".")[0].isdigit() and int(v.split(".")[0])>=6 for v in os.listdir(d)): return True
        except Exception: pass
    return False

def _find_game_dir():
    """Pasta REAL do jogo (onde vive GameAssembly.dll). Ordem: pastas do exe (caso o exe esteja junto
    do jogo), depois TODAS as bibliotecas Steam da maquina. A fonte DEFINITIVA e o proprio processo do
    jogo (ver Engine.attach -> _set_game_paths, que sobrepoe isto com o caminho REAL do modulo carregado).
    Antes isto tinha um caminho HARDCODED (d:\\SteamLibrary...) da MINHA maquina -> o painel nao achava o
    jogo no PC dos outros (dll_hash None -> offsets vazios). Foi o bug que pegou o amigo do usuario."""
    cands=[HERE, os.path.dirname(HERE), os.getcwd()]
    for lib in _steam_libraries():
        cands.append(os.path.join(lib,"steamapps","common","TaskbarHero"))
        cands.append(os.path.join(lib,"steamapps","common","TaskbarHero".lower()))
    seen=set()
    for d in cands:
        if not d or d in seen: continue
        seen.add(d)
        try:
            if os.path.exists(os.path.join(d,"GameAssembly.dll")): return d
        except Exception: pass
    return HERE
GAME_DIR=_find_game_dir()
GA_PATH=os.path.join(GAME_DIR,"GameAssembly.dll")
META_PATH=os.path.join(GAME_DIR,"TaskBarHero_Data","il2cpp_data","Metadata","global-metadata.dat")
def _set_game_paths(dll_path):
    """Fixa os caminhos do jogo a partir do caminho REAL do GameAssembly.dll (vindo do processo vivo).
    E a fonte mais confiavel: nao depende de onde o jogo esta instalado."""
    global GAME_DIR, GA_PATH, META_PATH
    try:
        if dll_path and os.path.exists(dll_path):
            GA_PATH=dll_path; GAME_DIR=os.path.dirname(dll_path)
            META_PATH=os.path.join(GAME_DIR,"TaskBarHero_Data","il2cpp_data","Metadata","global-metadata.dat")
            return True
    except Exception: pass
    return False
DUMPER=P("tools","Il2CppDumper","Il2CppDumper.exe")
CACHE=P("cache")
BUNDLED_CACHE=os.path.join(getattr(sys,"_MEIPASS",HERE),"cache")   # offsets embutidos no exe (read-only)
PROC="TaskBarHero.exe"; MOD="GameAssembly.dll"; STEAM_APPID="3678970"
EXE=P("TaskBarHero.exe")

# ---------- offsets CONSTANTES (estaveis entre updates de codigo) ----------
STATIC_FIELDS_OFF=0xB8
PSTAT_CHAIN=[0xB8,0x40,0x10,0x20,0x18]          # [slot] -> ... -> objeto de stats -> +campo
STATS={ # nome:(offset,tipo) tipo 'f'=float 'd'=double
 "Attack Damage":(0x3C,'f'),"Attack Speed":(0x4C,'f'),"Critical Chance":(0x5C,'f'),
 "Critical Damage":(0x6C,'f'),"Cooldown Reduction":(0xCC,'f'),"Cast Speed":(0x338,'d'),
 "Physical Damage":(0x1AC,'f'),"Fire Damage":(0x1BC,'f'),"Cold Damage":(0x1CC,'f'),
 "Lightning Damage":(0x1DC,'f'),"Chaos Damage":(0x1EC,'f'),"Max Hp":(0x7C,'f'),
 "Armor":(0x8C,'f'),"Dodge Chance":(0x12C,'f'),"Block Chance":(0x13C,'f'),
 "All Element Resistance":(0x36C,'f'),"Hp Regen /Sec":(0x198,'d'),"Dmg Absorption":(0x2AC,'f'),
 "Dmg Reduction":(0x24C,'f'),"Movement Speed":(0x9C,'f'),"Area of Effect %":(0xA8,'d'),
 "Area of Effect Damage":(0x398,'d'),"Add HP/Kill":(0x3CC,'f'),"Life Leech":(0x17C,'f'),
 "Skill Heal":(0x348,'d')}
STAGE_CHAIN=[0xB8,0x88,0x10]                    # [slot] -> StageInfoData
STAGE_FIELDS={"Act":0x48,"StageNo":0x4C,"StageLevel":0x50,"WaveAmount":0x54,
 "WaveMonsterAmount":0x58,"MonsterDropItemKey":0x68,"FirstClearDropKey":0x6C,
 "MonsterDropItemRate":0x70,"BossDropItemRate":0x74,"BossDropItemKey":0x78,
 "BossMonsterKey":0x7C,"BossDamageMultiplier":0x80,"BossGoldMultiplier":0x84,
 "BossExpMultiplier":0x88,"BossHpMultiplier":0x8C,"BossScale":0x90,
 "SoulStoneItemKey":0x94,"SoulStoneAmount":0x98}
INV_PSD_OFF=0x28; INV_LIST_OFF=0xA8; ITEMSAVE_KEY_OFF=0x10   # bau->PlayerSaveData->List<ItemSaveData>
RUNE_LIST_OFF=0x80          # PlayerSaveData.RuneSaveData (List<RuneSaveData{RuneKey@0x10,Level@0x14}>); prefira self.sym["PlayerSaveData.RuneSaveData"]
SYNTH_MIN_LVL=65; SYNTH_MAX_LVL=80   # LEVEL GATE synthesis: so funde se besu.MaxResultLevel>=65 (dropdown do cubo em ~65-80); nivel baixo nao funde

AOB_GODMODE="57 48 83 EC 50 80 3D ?? ?? ?? ?? ?? 41 0F ?? ?? 48 8B DA"
AOB_PSTAT="48 8B 05 ?? ?? ?? ?? 83 B8 E4 00 00 00 00 75 ?? 48 8B C8 E8 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 8B 80 B8 00 00 00 48 8B 48 20 48 85 C9 74 ?? 48 8B 15 ?? ?? ?? ?? E8"
AOB_STAGE="48 8B 05 ?? ?? ?? ?? 48 8B 80 B8 00 00 00 48 8B 88 88 00 00 00"
GRA_ORIG=b'\x48\x89\x5c\x24\x08'                # prologo de Monster.gra (hitkill)

# RVAs conhecidos por build (fallback rapido; se o hash nao bater, re-dumpa sozinho)
KNOWN_BUILDS={
 # md5(2MB) : {gra, bau_ti, ynj:[...], inv_class}. bau_ti None = resolve em runtime pelo inv_class.
 "8d3768c21857":{"gra":0xC29EC0,"bau_ti":0x5E3F908,"ynj":[0x7062F0,0x184C540],"inv_class":"bau","inv_psd_off":0x28,"inv_list_off":0xA8},
 "2c430296063a":{"gra":0xC1F730,"bau_ti":None,"ynj":[0x6F65F0],"inv_class":"bao","inv_klass_ti":0x5DFED88,"inv_psd_off":0x28,"inv_list_off":0xB0,"upd":0x9ACE70,"llx":0xA34D90,
   "iw":0x88BD30,"ilo":0x8B5880,"ipu":0x8C5B90,"imx":0x8BA060,"inf":0x8BA970,"ili":0x8B4F50,"iog":0x8BFBC0,"ioa":0x8BE8B0,"ima":0x8B6DB0,"llm":0xA341C0,"iuw":0x901690,"izb":0x915830,"inv_slots_off":0x88,"stash_off":0x90,"cube_slot":0x5DD2A30},
}

# ============================= AUTO-OFFSETS =============================
def dll_hash():
    try:
        with open(GA_PATH,"rb") as f: return hashlib.md5(f.read(2_000_000)).hexdigest()[:12]
    except Exception: return None

_GV={}
def game_version():
    """Versao do JOGO (bundleVersion do Unity, em globalgamemanagers) — ex '1.00.28'.
    NAO confundir com a versao da ENGINE (6000.x, que e o que o .exe reporta e fica no header).
    String do Unity = [int32 len][bytes] -> ancora nesse formato em vez de offset fixo (sobrevive
    a update do jogo). Cacheado por build."""
    h=dll_hash() or "x"
    if h in _GV: return _GV[h]
    v=None
    try:
        with open(os.path.join(GAME_DIR,"TaskBarHero_Data","globalgamemanagers"),"rb") as f:
            d=f.read(300_000)
        for m in re.finditer(rb'([\x04-\x14])\x00\x00\x00', d):
            L=m.group(1)[0]; s=d[m.end():m.end()+L]
            try: t=s.decode("ascii")
            except Exception: continue
            if re.fullmatch(r'\d+\.\d+[\d.]*', t) and not t.startswith("6000."):   # 6000.x = engine
                v=t; break
    except Exception: pass
    _GV[h]=v
    return v

def _extract_from_dump(ddir):
    """Extrai RVAs por ancoras ESTAVEIS (nomes nao-ofuscados + vtable slots),
    imune a ofuscacao renomear classes/metodos a cada build."""
    out={"gra":None,"bau_ti":None,"ynj":[],"inv_class":None}
    sj=os.path.join(ddir,"script.json"); cs=os.path.join(ddir,"dump.cs")
    try: src=open(cs,encoding="utf-8",errors="ignore").read()
    except Exception: src=""
    try: txt=open(sj,encoding="utf-8",errors="ignore").read()
    except Exception: txt=""
    # ynj (ACTk) — nome estavel (CodeStage). regex tolerante a espaco/quebra (JSON pretty-printed)
    out["ynj"]=[int(a) for a in re.findall(r'"Address":\s*(\d+),\s*"Name":\s*"[^"]*\$\$ynj"',txt)]
    # gra (hitkill) — Monster (nome estavel) + Slot 45 + (DamageInfo a, bool b = False)
    mc=re.search(r'\bclass Monster\s*:',src)
    if mc:
        seg=src[mc.start():mc.start()+45000]
        mm=(re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*Slot: 45[^\n]*\n\s*public (?:override |virtual )?void \w+\(DamageInfo a, bool b = False\)',seg)
            or re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*public (?:override |virtual )?void \w+\(DamageInfo a, bool b = False\)',seg))
        if mm: out["gra"]=int(mm.group(1),16)
    # inventario — classe que segura 'PlayerSaveData <campo>; // 0xNN' (nome+offset dinamicos)
    mf=re.search(r'private PlayerSaveData \w+; // (0x[0-9A-Fa-f]+)',src)
    if mf:
        out["inv_psd_off"]=int(mf.group(1),16)
        before=src[:mf.start()]; last=None
        for last in re.finditer(r'public class (\w+) : nq<\1>',before): pass
        if last:
            X=last.group(1); out["inv_class"]=X
            mt=re.search(r'"Address":\s*(\d+),\s*"Name":\s*"nq<'+re.escape(X)+r'>_TypeInfo"',txt)
            if mt: out["bau_ti"]=int(mt.group(1))   # generico (da a instancia direto via static)
            mk=re.search(r'"Address":\s*(\d+),\s*"Name":\s*"'+re.escape(X)+r'_TypeInfo"',txt)
            if mk: out["inv_klass_ti"]=int(mk.group(1))  # classe concreta -> klass -> scan da instancia
    # offset do itemSaveDatas em PlayerSaveData (shifta entre builds!)
    ml=re.search(r'List<ItemSaveData> \w+; // (0x[0-9A-Fa-f]+)',src)
    if ml: out["inv_list_off"]=int(ml.group(1),16)
    # izb = ItemInfoData por key (getter estatico). Da GRADE@0x38 + ItemSynthesisType@0x48 + Level@0x6C
    # REAIS (a contagem por familia chutava errado Gear/Accessory). 1o 'public static ItemInfoData X(int a)'.
    mi=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*public static ItemInfoData \w+\(int a\)',src)
    if mi: out["izb"]=int(mi.group(1),16)
    # AUTO-CAIXA — upd=InputManager.Update (dispatcher per-frame main-thread, nomes estaveis)
    mu=re.search(r'\bclass InputManager\b',src)
    if mu:
        seg=src[mu.start():mu.start()+4000]
        mm=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*private void Update\(\)',seg)
        if mm: out["upd"]=int(mm.group(1),16)
    # llx=StageBox click handler: void X(PointerEventData.InputButton a) (classe StageBox estavel)
    ms=re.search(r'\bclass StageBox\b',src)
    if ms:
        seg=src[ms.start():ms.start()+22000]
        mm=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*private void \w+\(PointerEventData\.InputButton a\)',seg)
        if mm: out["llx"]=int(mm.group(1),16)
    # iuw=contagem REAL abrivel (uw.tw): a classe do box-store tem o campo distintivo
    # Dictionary<EBoxType, Dictionary<ulong, int>>; iuw = 'public static int X(EBoxType a)' nela.
    # So p/ FEEDBACK do log — se nao achar, o loop cai na rotacao cega (abre igual, sem avisar).
    mtw=re.search(r'Dictionary<EBoxType,\s*Dictionary<(?:ulong|System\.UInt64),\s*int>>',src)
    if mtw:
        cs=src.rfind('class ',0,mtw.start()); ce=src.find('\n}\n',mtw.start())
        if cs>=0 and ce>mtw.start():
            seg2=src[cs:ce]
            mm=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*public static int \w+\(EBoxType a\)',seg2)
            if mm: out["iuw"]=int(mm.group(1),16)      # AMBIGUO (12 candidatos) -> so reserva; ver abaixo
    # RECIPE TYPE (obfuscado, muda entre builds: ux@2c43 -> uw@c824). Ancora estavel:
    # 'Dictionary<ERecipeType, List<X>>' com X curto/lowercase = o tipo da recipe.
    mrt=re.search(r'Dictionary<ERecipeType, List<([a-z]{1,4})>>',src)
    RT=mrt.group(1) if mrt else "ux"                                          # fallback 2c43
    # imx=Cube trigger synthesis/craft: <TriggerCurrentRecipeLogic> (nome preservado); namespace FLEXIVEL
    mm=re.search(r'\[AsyncStateMachine\(typeof\(\w+\.Cube\.<TriggerCurrentRecipeLogic>[^\n]*\n\s*// RVA: (0x[0-9A-Fa-f]+)',src)
    if mm: out["imx"]=int(mm.group(1),16)
    # === SYNTHESIS (Cube): ancora na classe 'Cube' (nome estavel) + assinaturas UNICAS dos metodos ===
    mcube=re.search(r'public static class \S*\bCube\b',src)
    if mcube:
        seg=src[mcube.start():mcube.start()+95000]
        def _rb(sig):   # RVA da linha 'RVA:' imediatamente antes de uma assinatura
            m=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*'+sig,seg)
            return int(m.group(1),16) if m else None
        out["ilo"]=_rb(r'public static void \w+\(EItemSynthesisType a\)')      # set tipo
        out["ili"]=_rb(r'public static bool \w+\(EGradeType a\)')             # set grade (evento)
        out["inf"]=_rb(r'private static void \w+\(%s a\)'%RT)                 # set recipe (bery) — usa RECIPE TYPE
        out["ima"]=_rb(r'public static bool \w+\(%s a\)'%RT)                  # set variante de nivel — usa RECIPE TYPE
        out["iog"]=_rb(r'private static void \w+\(int a, CubeInData b\)')     # builder recipe-grade
        out["ioa"]=_rb(r'public static EAddCubeResult \w+\(ESlotType a, int b\)')  # add item ao cubo
        # ipu = 'public static void X()' IMEDIATAMENTE antes de ipv (assinatura unica: retorna BucketCountResult)
        mp=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*public static void \w+\(\) \{ \}\s*\n\s*// RVA:[^\n]*\n\s*private static \S*BucketCountResult',seg)
        if mp: out["ipu"]=int(mp.group(1),16)
    # === MOVE (auto-stash): ra.iw(MoveRequest, Action<...>) — MoveRequest e assinatura distintiva ===
    miw=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*public \w+ \w+\(MoveRequest a, Action<\w+> b\)',src)
    if miw: out["iw"]=int(miw.group(1),16)
    # nome da CLASSE do move-manager (singleton nq<X>) — ofuscado, muda entre builds (ra@2c43 -> qz@c824).
    # ancora: ultima 'public class X : nq<X>' ANTES do metodo iw(MoveRequest, Action<>).
    if miw:
        before=src[:miw.start()]; rc=None
        for rc in re.finditer(r'public class (\w+) : nq<\1>',before): pass
        if rc: out["ra_class"]=rc.group(1)
    # === offsets de inventario/stash em PlayerSaveData (shiftam entre builds) ===
    mio=re.search(r'List<InventorySaveData> \w+; // (0x[0-9A-Fa-f]+)',src)
    if mio: out["inv_slots_off"]=int(mio.group(1),16)
    mso=re.search(r'List<StashSaveData> \w+; // (0x[0-9A-Fa-f]+)',src)
    if mso: out["stash_off"]=int(mso.group(1),16)
    # === contador de caixas: resolve pelo CODIGO do llx (a assinatura no dump e ambigua) ===
    if out.get("llx"):
        real=_iuw_from_llx(out["llx"])
        if real: out["iuw"]=real
    # === mapa/navegacao de estagio (ancora StageNode) — opcional: se falhar, o resto do painel segue ===
    try: out.update(_stage_anchors(ddir))
    except Exception: pass
    # === offsets de campo/metodo do CUBO/ITEM/STAGE/SAVE + cobertura maxima (auto-atualizam no re-dump) ===
    try:
        da=_data_anchors(ddir)
        for k,v in da.items():
            if v is not None: out[k]=v          # nao sobrescreve com None (mantem o extraido antes)
    except Exception: pass
    return out

def _redump():
    """Roda Il2CppDumper na build atual -> extrai RVAs -> apaga o dump grande."""
    import tempfile, shutil
    td=os.path.join(CACHE,"_dump_tmp")
    try:
        if os.path.isdir(td): shutil.rmtree(td,ignore_errors=True)
        os.makedirs(td,exist_ok=True)
        env=dict(os.environ, DOTNET_ROLL_FORWARD="Major")
        subprocess.run([DUMPER,GA_PATH,META_PATH,td],cwd=os.path.dirname(DUMPER),
                       env=env,timeout=300,creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
        got=_extract_from_dump(td)
        return got
    except Exception as e:
        return {"gra":None,"bau_ti":None,"ynj":[],"error":str(e)}
    finally:
        shutil.rmtree(td,ignore_errors=True)

# simbolos CRITICOS que as automacoes precisam — a extracao so e "boa" se TODOS resolverem.
# (llm/cmd11 e codigo morto -> nao entra; inv_klass_ti|bau_ti = 1 dos 2 basta)
def _dll_bytes(rva, n):
    """n bytes ORIGINAIS de um RVA, lidos do GameAssembly.dll NO DISCO (RVA->offset via PE). O arquivo
    nunca tem os nossos patches, e nao precisa do jogo aberto."""
    try:
        with open(GA_PATH,"rb") as f: data=f.read()
        e=int.from_bytes(data[0x3C:0x40],"little")                       # e_lfanew
        nsec=int.from_bytes(data[e+6:e+8],"little")
        secs=e+0x18+int.from_bytes(data[e+0x14:e+0x16],"little")         # +SizeOfOptionalHeader
        for i in range(nsec):
            s=secs+i*40
            vsz=int.from_bytes(data[s+8:s+12],"little")
            va=int.from_bytes(data[s+12:s+16],"little")
            raw=int.from_bytes(data[s+20:s+24],"little")
            if va<=rva<va+max(vsz,1):
                off=rva-va+raw
                return data[off:off+n]
    except Exception: pass
    return None

def _iuw_from_llx(llx_rva):
    """RVA do contador REAL de caixas esperando = o call que o proprio llx testa com 'test eax,eax /
    jle' antes de abrir (llx: `if (count(boxType) <= 0) return;`).

    Ancorar por assinatura no dump NAO serve aqui: a classe uu tem 12 metodos `static int X(EBoxType)`
    e a extracao pegava o PRIMEIRO (itq) em vez do certo (iuv). itq e outro contador, que nunca zera
    -> o bot achava que tinha caixa pra sempre e clicava a cada tick eternamente. Ancorar no
    COMPORTAMENTO do codigo (quem o llx chama) e imune tanto a rename quanto a ambiguidade."""
    try:
        from capstone import Cs, CS_ARCH_X86, CS_MODE_64
        code=_dll_bytes(llx_rva, 320)
        if not code: return None
        ins=list(Cs(CS_ARCH_X86, CS_MODE_64).disasm(code, llx_rva))     # addr=RVA -> alvos ja saem em RVA
        last=None
        for k,i in enumerate(ins):
            if i.mnemonic=="call" and i.op_str.startswith("0x"): last=int(i.op_str,16)
            elif (i.mnemonic=="test" and i.op_str=="eax, eax" and last     # o gate iyd() usa 'test al, al'
                  and k+1<len(ins) and ins[k+1].mnemonic in ("jle","jg")): # -> nao confunde
                return last
    except Exception: pass
    return None

# ================= ANCORAS DE ESTAGIO (mapa + navegacao) =================
# Regra aprendida na dor (ver o bug do contador de caixas): NUNCA ancorar em nome ofuscado nem em
# assinatura ambigua. Dentro da classe de estagio ha 15 "static void X(int)", 11 "static bool X(int)"
# e 5 "(StageCache, Action<bool>)" — assinatura pura seria sorteio.
# A ancora e a classe StageNode: o Unity NAO pode renomea-la (MonoBehaviour e serializado por nome em
# cena/prefab), e o handler de clique dela chama EXATAMENTE os 4 alvos, com assinaturas distintas
# entre si -> desambiguacao total. Cross-checks independentes confirmam cada um.
class _PE:
    """GameAssembly.dll do disco, com cache (o resolver disassembla muitas funcoes)."""
    def __init__(self, path):
        self.data=open(path,"rb").read()
        e=struct.unpack_from("<I",self.data,0x3C)[0]
        nsec=struct.unpack_from("<H",self.data,e+6)[0]; opt=struct.unpack_from("<H",self.data,e+20)[0]
        self.secs=[]
        for i in range(nsec):
            b=e+24+opt+40*i
            self.secs.append((struct.unpack_from("<I",self.data,b+12)[0], struct.unpack_from("<I",self.data,b+8)[0],
                              struct.unpack_from("<I",self.data,b+20)[0], struct.unpack_from("<I",self.data,b+16)[0]))
    def read(self, rva, n):
        for va,vsz,raw,rsz in self.secs:
            if va<=rva<va+max(vsz,rsz): return self.data[raw+(rva-va):raw+(rva-va)+n]
        return b""

class _Klass:
    __slots__=("name","decl","fields","methods")
    def __init__(self,name,decl): self.name=name; self.decl=decl; self.fields=[]; self.methods=[]

_A_RVA=re.compile(r'//\s*RVA:\s*0x([0-9A-Fa-f]+)')
_A_CLS=re.compile(r'^(?:\[.*\]\s*)?(?:public|private|internal|protected)?\s*(?:static\s+|sealed\s+|abstract\s+)*class\s+([^\s:/]+)')
_A_FLD=re.compile(r'^\s*(?:\[[^\]]*\]\s*)?(?:public|private|internal|protected)\s+(?:static\s+)?(?:readonly\s+)?(.+?)\s+([A-Za-z_<>][\w<>]*);\s*//\s*0x([0-9A-Fa-f]+)')
_A_MTH=re.compile(r'^\s*(?:\[[^\]]*\]\s*)?(?:public|private|internal|protected)\s+(.*?\S)\s*\{\s*\}\s*$')
_A_SIG=re.compile(r'^(?:(static)\s+)?(?:(virtual|override|abstract)\s+)?([\w<>,\.\[\]]+)\s+([\w<>\.]+)\(([^)]*)\)$')

def _a_parse_dump(path):
    classes=[]; cur=None; pend=None
    with open(path,"r",encoding="utf-8",errors="replace") as fh:
        for line in fh:
            m=_A_RVA.search(line)
            if m and line.lstrip().startswith("//"): pend=int(m.group(1),16); continue
            s=line.rstrip("\n")
            if s.startswith(("public ","private ","internal ","protected ")) and " class " in s:
                cm=_A_CLS.match(s)
                if cm: cur=_Klass(cm.group(1),s); classes.append(cur); pend=None; continue
            if cur is None: continue
            fm=_A_FLD.match(s)
            if fm and "//" in s and "(" not in fm.group(1):
                t=fm.group(1).strip()
                cur.fields.append((t.replace("static ","").strip(), fm.group(2), int(fm.group(3),16),
                                   "static " in fm.group(1) or " static " in s))
                continue
            mm=_A_MTH.match(s)
            if mm and pend is not None: cur.methods.append((pend,mm.group(1))); pend=None
    return classes

def _a_sig(sig):
    m=_A_SIG.match(sig.replace("readonly ","").strip())
    if not m: return None
    args=[]; raw=m.group(5).strip()
    if raw:
        d=0; cur=""
        for ch in raw:
            if ch in "<([": d+=1
            elif ch in ">)]": d-=1
            if ch=="," and d==0: args.append(cur.strip()); cur=""
            else: cur+=ch
        args.append(cur.strip())
        args=[re.sub(r'\s*=.*$','',a).rsplit(" ",1)[0].strip() if " " in re.sub(r'\s*=.*$','',a) else re.sub(r'\s*=.*$','',a).strip() for a in args]
    return (m.group(1)=="static", m.group(3), m.group(4), args)

def _a_extent(addrs, rva):
    i=bisect.bisect_right(addrs,rva)
    end=addrs[i] if i<len(addrs) else rva+0x400
    return max(0x10, min(end-rva, 0x4000))

def _a_dis(pe, addrs, rva):
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    md=Cs(CS_ARCH_X86,CS_MODE_64); md.detail=False
    return list(md.disasm(pe.read(rva,_a_extent(addrs,rva)), rva))   # address == RVA

def _a_flow(pe, addrs, rva, kinds=("call","jmp")):
    return [(i.mnemonic,int(i.op_str,16)) for i in _a_dis(pe,addrs,rva)
            if i.mnemonic in kinds and re.fullmatch(r'0x[0-9a-f]+', i.op_str or "")]

def _a_pick(callees, ret_want, args_want, label, pe, addrs):
    """1 hit -> ok. N hits CLONES (mesma forma de codigo) -> qualquer um serve (o ofuscador duplica a
    mesma impl). N hits DIFERENTES -> erro: melhor falhar que entrar no estagio errado."""
    hits=[]
    for rva,sig in callees.items():
        p=_a_sig(sig)
        if not p: continue
        st,ret,nm,args=p
        if not st or ret!=ret_want or len(args)!=len(args_want): continue
        if all(re.fullmatch(w,a) for w,a in zip(args_want,args)): hits.append((rva,sig))
    if len(hits)==1: return hits[0][0]
    if len(hits)>1:
        shapes={tuple(i.mnemonic for i in _a_dis(pe,addrs,r)) for r,_ in hits}
        if len(shapes)==1: return sorted(hits)[0][0]      # clones -> equivalentes
    raise AssertionError("%s ambiguo (%d)"%(label,len(hits)))

def _stage_anchors(ddir):
    """{jgk,jgq,jgc,jgd,uo_ti,bal_ti,stage_off,uo_*} resolvidos por ancora estavel. Levanta se ambiguo."""
    classes=_a_parse_dump(os.path.join(ddir,"dump.cs"))
    sj=json.load(open(os.path.join(ddir,"script.json"),encoding="utf-8"))
    pe=_PE(GA_PATH); addrs=sorted(set(sj["Addresses"])); out={}
    ti=lambda n: next((m["Address"] for m in sj["ScriptMetadata"] if m["Name"]==n), None)
    # uu.uo = UNICA classe estatica com static List<StageCache> E static Dictionary<int,StageCache>
    uos=[k for k in classes if " static class " in k.decl
         and any(re.fullmatch(r'List<[\w\.]*\.?StageCache>',f[0]) for f in k.fields if f[3])
         and any(re.fullmatch(r'Dictionary<int,\s*[\w\.]*\.?StageCache>',f[0]) for f in k.fields if f[3])]
    if len(uos)!=1: raise AssertionError("uo ambiguo (%d)"%len(uos))
    uo=uos[0]; out["uo_ti"]=ti(uo.name+"_TypeInfo")
    st=[f for f in uo.fields if f[3]]
    for typ,nm,off,_ in st:
        if re.fullmatch(r'Dictionary<int,\s*[\w\.]*\.?StageCache>',typ) and "uo_dict" not in out: out["uo_dict"]=off
        elif re.fullmatch(r'[\w\.]*\.?StageCache',typ) and "uo_cur_cache" not in out: out["uo_cur_cache"]=off
    obs=[f[2] for f in st if f[0]=="ObscuredInt"]            # ordem: max, cur_key, cur_wave
    if len(obs)>=3: out["uo_max"],out["uo_cur"],out["uo_wave"]=obs[0],obs[1],obs[2]
    # bal: o campo 'stageInfoData' NAO e ofuscado (tabela desserializada por nome)
    bals=[(k,off) for k in classes for typ,nm,off,_ in k.fields if nm=="stageInfoData" and typ=="List<StageInfoData>"]
    if len(bals)!=1: raise AssertionError("bal ambiguo (%d)"%len(bals))
    out["bal_ti"]=ti("nq<%s>_TypeInfo"%bals[0][0].name); out["stage_off"]=bals[0][1]
    # HUB StageNode (nome que o Unity nao renomeia) -> os 4 alvos dentro de uo
    uom={r:s for r,s in uo.methods}
    cal={}
    for k in [x for x in classes if x.name=="StageNode" or x.name.startswith("StageNode.")]:
        for rva,_ in k.methods:
            for kind,tgt in _a_flow(pe,addrs,rva):
                if tgt in uom: cal[tgt]=uom[tgt]
    SC=r'[\w\.]*\.?StageCache'
    out["jgk"]=_a_pick(cal,"void",[r'int'],"jgk",pe,addrs)                       # entrar na fase
    out["jgq"]=_a_pick(cal,"bool",[r'int'],"jgq",pe,addrs)                       # liberado?
    out["jgc"]=_a_pick(cal,"EStageEnterResultType",[SC],"jgc",pe,addrs)          # validador (soulstone/bau)
    out["jgd"]=_a_pick(cal,"void",[SC,r'Action<bool>'],"jgd",pe,addrs)           # ACTBOSS (reserva a pedra)
    # cross-check: jgq tem a constante 1101 (Normal 1-1) hardcoded -> confirma o alvo
    if not any(i.mnemonic in ("cmp","mov") and (i.op_str or "").endswith("0x44d") for i in _a_dis(pe,addrs,out["jgq"])):
        raise AssertionError("jgq sem a constante 1101 — ancora suspeita")
    return out

# classes de DADOS cujos NOMES DE CAMPO sao preservados (nao ofuscados) -> extraio TODOS os campos por
# nome como "<Classe>.<Campo>" = offset. Cobertura maxima: mesmo campos que ainda nao usamos ficam
# disponiveis e auto-atualizam. (as *InfoData/SaveData sao desserializadas por nome -> nomes estaveis.)
_DATA_CLASSES=["StageInfoData","ItemInfoData","SynthesisRecipeInfoData","CraftingRecipeInfoData",
    "CommonSaveData","PlayerSaveData","ItemSaveData","InventorySaveData","StashSaveData","HeroSaveData",
    "AccountSaveData","SettingSaveData","HeroInfoData","GearInfoData","MonsterInfoData","SkillInfoData",
    "GradeInfoData","DropInfoData","SynthesisDropInfoData","CurrencyInfoData","PetInfoData","AttributeInfoData",
    "GearTypeInfoData","ItemLevelScaleInfoData","ItemTypeScaleInfoData","GearTypeScaleInfoData","CubeRecipeInfoData",
    "ExtractionCostInfoData","CubeInData"]

def _is_clear_name(nm):
    """nome de campo 'em claro' (PascalCase/camelCase/underscore) vs ofuscado (token curto minusculo)."""
    return bool(re.search(r'[A-Z]',nm) or "_" in nm) and not re.fullmatch(r'[a-z]{1,6}\d?',nm)

def _data_anchors(ddir):
    """Extrai da DLL (dump.cs + script.json), de forma ESTAVEL a ofuscacao, todos os offsets de campo/
    metodo que estavam hardcoded no codigo. Auto-atualiza no re-dump. Nunca ancora em nome ofuscado:
    classes de dados = por NOME de campo (preservado); uu.Cube (campos ofuscados) = por TIPO+ordem."""
    out={}
    try:
        classes=_a_parse_dump(os.path.join(ddir,"dump.cs"))
        byname={k.name:k for k in classes}
    except Exception:
        return out
    try:
        sj=json.load(open(os.path.join(ddir,"script.json"),encoding="utf-8"))
        ti=lambda n: next((m["Address"] for m in sj["ScriptMetadata"] if m["Name"]==n), None)
    except Exception:
        ti=lambda n: None
    # ---------- uu.Cube (campos ofuscados -> por TIPO estatico + ordem) ----------
    cube=byname.get("uu.Cube")
    def cbt(t,idx=0):
        if not cube: return None
        fs=[f[2] for f in sorted(cube.fields,key=lambda x:x[2]) if f[0]==t and f[3]]
        return fs[idx] if idx<len(fs) else None
    for sym,typ,idx in (("cube_grade","EGradeType",0),("cube_type","EItemSynthesisType",0),
                        ("cube_inlist","List<CubeInData>",0),("cube_recipe","SynthesisRecipeInfoData",0),
                        ("cube_bers","Dictionary<ERecipeType, List<uw>>",0),("cube_beru","Dictionary<ERecipeType, uw>",0),
                        ("cube_active","uw",0),("cube_lvrecipe","uw",1),("cube_busy","bool",0),
                        ("cube_resultevt","Action<ECubeSynthesisResult>",0)):
        v=cbt(typ,idx)
        if v is not None: out[sym]=v
    # ---------- ilx = 1o (menor RVA) static void(ERecipeType) na Cube ----------
    if cube:
        cand=sorted(rva for rva,sig in cube.methods
                    if (p:=_a_sig(sig)) and p[0] and p[1]=="void" and p[3]==["ERecipeType"])
        if cand: out["ilx"]=cand[0]
    # ---------- UIManager: typeinfo + ui_main (nome preservado) ----------
    v=ti("nq<UIManager>_TypeInfo")
    if v: out["uimgr_ti"]=v
    uim=byname.get("UIManager")
    if uim:
        for typ,nm,off,st in uim.fields:
            if nm=="ui_main" and not st: out["uimain"]=off
    # ---------- offsets nomeados que o codigo usa (por nome, classes-chave) ----------
    def fld(cn,fieldname):
        k=byname.get(cn)
        return next((off for typ,nm,off,st in k.fields if nm==fieldname and not st),None) if k else None
    NAMED={"recipe_minlvl":("SynthesisRecipeInfoData","MinResultLevel"),
           "recipe_maxlvl":("SynthesisRecipeInfoData","MaxResultLevel"),
           "iteminfo_type":("ItemInfoData","ITEMTYPE"),"iteminfo_grade":("ItemInfoData","GRADE"),
           "iteminfo_synth":("ItemInfoData","ItemSynthesisType"),"iteminfo_level":("ItemInfoData","Level"),
           "psd_common_off":("PlayerSaveData","commonSaveData"),
           "commonsave_usestorage":("CommonSaveData","useStorage"),
           "commonsave_curstage":("CommonSaveData","currentStageKey"),
           "commonsave_maxstage":("CommonSaveData","maxCompletedStage"),
           "itemsave_key":("ItemSaveData","ItemKey"),"itemsave_uid":("ItemSaveData","ItemUniqueId")}
    for sym,(cn,fn) in NAMED.items():
        v=fld(cn,fn)
        if v is not None: out[sym]=v
    # ---------- COBERTURA MAXIMA: todos os campos em claro das classes de dados ----------
    for cn in _DATA_CLASSES:
        k=byname.get(cn)
        if not k: continue
        for typ,nm,off,st in k.fields:
            if not st and _is_clear_name(nm): out["%s.%s"%(cn,nm)]=off
    return out

# handlers de UI que NAO sao pinaveis por assinatura estatica (dezenas de void X() vazios em UI_Main/
# UIManager). Ficam por build ate haver resolucao ao vivo; na build atual vem daqui. Se o jogo atualizar
# e nao estiverem aqui, o auto-fuse so nao AUTO-ABRE o cubo (degradacao graciosa) — o resto auto-resolve.
_KNOWN_UI_HANDLERS={"c824ed7a2bb1":{"eby":0x839BB0,"hgr":0xC362A0}}

_EXTRACT_VER=5   # BUMPAR sempre que a extracao mudar: invalida os caches antigos. Sem isso um offset
                 # errado fica gravado no disco e o fix nao chega em quem ja rodou o painel.
_CRIT_SYMS=("gra","upd","llx","iw","ra_class","ilo","ipu","imx","inf","ili","iog","ioa","ima","iuw","izb","inv_slots_off","stash_off")
def _offsets_ok(got):
    """True se o dict tem TODOS os simbolos criticos (nao aceita extracao parcial que quebra features)."""
    if not isinstance(got,dict): return False
    if not all(got.get(k) for k in _CRIT_SYMS): return False
    return bool(got.get("inv_klass_ti") or got.get("bau_ti"))   # singleton do inventario (1 dos 2)

def _missing_syms(got):
    m=[k for k in _CRIT_SYMS if not (isinstance(got,dict) and got.get(k))]
    if isinstance(got,dict) and not (got.get("inv_klass_ti") or got.get("bau_ti")): m.append("inv_singleton")
    return m

def resolve_symbols(log=lambda m:None):
    """Resolve os offsets do build atual. Auto re-dumpa (com retry) se o jogo atualizou.
    NUNCA usa RVAs de outro build (isso quebraria tudo) — melhor parcial que errado."""
    os.makedirs(CACHE,exist_ok=True)
    h=dll_hash()
    if not h:
        log("!! nao consegui ler GameAssembly.dll — sem offsets (features off ate resolver).")
        return {"gra":None,"bau_ti":None,"ynj":[],"error":"no_dll_hash"}
    if h in KNOWN_BUILDS:
        return dict(KNOWN_BUILDS[h])
    cf=os.path.join(CACHE,"offsets_%s.json"%h)
    # offsets prontos p/ ESTE build, sem dumpar: 1) cache gravado localmente  2) EMBUTIDOS no exe.
    # Os embutidos sao a peca-chave da robustez: o exe ja sai com os offsets do build que ele acompanha,
    # entao NUNCA precisa do Il2CppDumper (que exige .NET 6, ausente na maioria das maquinas) p/ o build
    # atual. So cai no dump se o jogo atualizar pra um build que o exe ainda nao conhece.
    for src in (cf, os.path.join(BUNDLED_CACHE,"offsets_%s.json"%h)):
        try:
            if not os.path.exists(src): continue
            cached=json.load(open(src,encoding="utf-8"))
            if cached.get("_ver")!=_EXTRACT_VER:
                log("cache de offsets de extrator antigo (%s) — ignorando."%os.path.basename(src)); continue
            if _offsets_ok(cached):
                if src!=cf:                              # veio do bundle -> grava no cache gravavel
                    try: json.dump(cached,open(cf,"w"))
                    except Exception: pass
                return cached
            log("offsets incompletos em %s (%s)."%(os.path.basename(src),",".join(_missing_syms(cached))))
        except Exception: pass
    # precisa dumpar (build novo). Sem .NET 6 o Il2CppDumper nao roda -> avisa claro, nao falha calado.
    if not _dotnet6_ok():
        log("!! O jogo atualizou (build %s) e esta maquina NAO tem o .NET 6 pra re-extrair os offsets."%h)
        log("!! Baixe a versao mais nova do painel (cada release ja vem com os offsets do build novo):")
        log("!!   https://github.com/matheusbranhann/taskbarhero-bot/releases")
        return {"gra":None,"bau_ti":None,"ynj":[],"error":"needs_dotnet6"}
    log("Game updated (build %s) — re-dumping offsets automatically… (~30s)"%h)
    last={}
    for attempt in range(3):                            # retry: falhas transientes do dumper
        got=_redump()
        got.update(_KNOWN_UI_HANDLERS.get(h,{}))   # eby/hgr: nao pinaveis por assinatura -> por build (degrada gracioso se faltar)
        if _offsets_ok(got):
            got["_ver"]=_EXTRACT_VER
            try: json.dump(got,open(cf,"w"))
            except Exception: pass
            log("New offsets resolved and saved (build %s)."%h)
            return got
        last=got if isinstance(got,dict) else {}
        log("Re-dump tentativa %d incompleta (faltam: %s)."%(attempt+1,", ".join(_missing_syms(got)) or got.get("error","?")))
    # NAO caiu no _best() de proposito: RVAs de outro build quebram tudo. Retorna o parcial + erro.
    log("!! Re-dump nao resolveu todos os offsets. Recomendo checar o extrator. (parcial salvo p/ diagnostico)")
    try: json.dump(last,open(cf,"w"))
    except Exception: pass
    last["error"]=last.get("error") or "extracao_incompleta"
    return last

# ============================= PRECOS =============================
GW={"common":0,"normal":0,"uncommon":1,"rare":2,"legendary":3,"immortal":4,
    "arcana":5,"beyond":6,"celestial":7,"divine":8,"cosmic":9}
GRW={0:"Common",1:"Uncommon",2:"Rare",3:"Legendary",4:"Immortal",5:"Arcana",
     6:"Beyond",7:"Celestial",8:"Divine",9:"Cosmic"}
_MKT=re.compile(r'^(.*?)\s*\((\w+)\)\s*([A-Za-z])?\s*$')
def _norm(s): return re.sub(r'[^a-z0-9 ]','',(s or '').lower()).strip()

class Prices:
    def __init__(self):
        self.pr={}; self.byg=collections.defaultdict(dict); self.graded=set(); self.names=[]
        self.load()
    def _ingest(self,name,p):
        """Adiciona 'Base (Grade) X' ou 'Base' (material) ao indice byg."""
        if p is None: return
        m=_MKT.match(name)
        if m:
            b=_norm(m.group(1)); gi=GW.get(m.group(2).lower())
            if gi is None: gi=-1
            else: self.graded.add(b)
        else: b=_norm(name); gi=-1
        self.byg[b][gi]=max(self.byg[b].get(gi,0.0),p)
    def load(self):
        try: self.pr=json.load(open(P("item_prices.json"),encoding="utf-8"))
        except Exception: self.pr={}
        self.byg=collections.defaultdict(dict); self.graded=set()
        try: mk=json.load(open(P("market_prices.json"),encoding="utf-8"))["items"]
        except Exception: mk={}
        for n,v in mk.items(): self._ingest(n,v.get("price_usd"))
        # FALLBACK: se o market_prices.json estiver vazio/quebrado (ex: rate-limit da
        # Steam apagou tudo), reconstroi o indice a partir do item_prices.json — assim
        # os precos NUNCA somem de vez.
        if len(self.byg)<50:
            for rec in self.pr.values():
                p=rec.get("price_usd")
                if p is None: continue
                self._ingest(rec.get("market_name") or rec.get("base") or "",p)
        self.names=list(self.byg.keys())
    def base_of_key(self,ik):
        rec=self.pr.get(str(ik)); return rec.get("base") if rec else None
    def resolve_base(self,texts):
        best=None; bestr=0.0
        for t in texts:
            nb=_norm(t)
            if not nb: continue
            if nb in self.byg: return nb
            c=difflib.get_close_matches(nb,self.names,1,0.60)
            if c:
                r=difflib.SequenceMatcher(None,nb,c[0]).ratio()
                if r>bestr and r>=0.70: bestr=r; best=c[0]
        return best
    def price(self,base,gi):
        if not base: return None,"sem"
        nb=_norm(base)
        if nb not in self.byg:
            c=difflib.get_close_matches(nb,self.names,1,0.72)
            if not c: return None,"sem"
            nb=c[0]
        g=self.byg[nb]
        if nb not in self.graded: return max(g.values()),"ok"
        if gi in g: return g[gi],"ok"
        return g[min(g,key=lambda k:abs(k-gi))],"aprox"

# ============================= ENGINE =============================
def parse_aob(s):
    b=bytearray(); m=bytearray()
    for t in s.split():
        if t=="??": b.append(0); m.append(0)
        else: b.append(int(t,16)); m.append(1)
    return bytes(b),bytes(m)

class Engine:
    def __init__(self, log=lambda m:None):
        self.log=log; self.pm=None; self.base=None; self.size=None; self.pid=None
        self.cache={}; self.sym={}; self.lock=threading.RLock()
        # estado desejado (controlado pela GUI)
        self.want={"actk":False,"god":False,"hitkill":False,"autobox":False,"autoitem":False,"autosynth":False,
                   "watchdog":False, "autoboss":False, "evolve":False,
                   "synth_maxgrade":2, "synth_types":{0,1,2}}   # auto-fuse: teto de grade (2=rare, default seguro) e tipos (0=Equip/1=Acess/2=Mat)
        self.stats={}       # nome -> valor (float) a forcar (manual)
        self.speed_stats={} # (legado, nao usado)
        self.stage={}       # campo -> int a forcar
        self.sh_mult=1.0    # SPEEDHACK de relogio desejado (1.0 = off)
        self.sh=None        # estado do hook instalado
        self.abx=None       # estado do dispatcher generico (InputManager.Update)
        self._abx_thr=None  # (legado)
        self._itm_thr=None  # (legado)
        self._auto_thr=None # thread UNICA do loop de automacao (caixa->stash->fuse)
        self._wd_thr=None   # thread do WATCHDOG (mantem o jogo aberto)
        self._wd_hold=False # True durante o restart: o tick() attacha mas NAO aplica nada (start limpo)
        self._disp_lock=threading.RLock()  # serializa comandos no dispatcher
        self._rc_lock=threading.RLock()    # serializa _remote_call (scratch compartilhado)
    # ---- attach ----
    def attach(self):
        try:
            import pymem, pymem.process
            pm=pymem.Pymem(PROC)
            ga=pymem.process.module_from_name(pm.process_handle,MOD)
            if ga and ga.lpBaseOfDll:
                _set_game_paths(getattr(ga,"filename",None))   # caminho REAL do DLL (funciona em qualquer PC)
                if pm.process_id!=self.pid:
                    self.pid=pm.process_id; self.cache={}; self.sh=None; self.abx=None   # hooks morreram com o processo antigo
                    self.sym=resolve_symbols(self.log)
                    self.log("attach PID=%d base=%#x"%(pm.process_id,ga.lpBaseOfDll))
                self.pm=pm; self.base=ga.lpBaseOfDll; self.size=ga.SizeOfImage
                self._load_offsets()                     # offsets de campo (cubo/item) do self.sym, com fallback c824
                if not self.sym.get("cube_slot") and self.sym.get("ilo"):   # build novo: resolve cube_slot ao vivo
                    cs=self._resolve_cube_slot()
                    if cs: self.sym["cube_slot"]=cs; self.log("cube_slot resolved live: %#x"%cs)
                return True
        except Exception:
            self.pm=None; self.pid=None
        return False
    def _load_offsets(self):
        """Offsets de campo do CUBO/ITEM lidos do self.sym (auto-extraidos da DLL por _data_anchors);
        fallback = valores da build c824. Assim, quando o jogo atualiza, o re-dump traz os offsets novos
        e o codigo passa a usa-los SEM hardcode (era o pedido: 'se o jogo atualizar, atualize sozinho')."""
        g=self.sym.get
        self.OFF={"inlist":g("cube_inlist",0x100),"active":g("cube_active",0x140),"busy":g("cube_busy",0x150),
                  "grade":g("cube_grade",0xC8),"beru":g("cube_beru",0xF0),"bers":g("cube_bers",0xE0),
                  "recipe":g("cube_recipe",0x240),"cubetype":g("cube_type",0x254),"lvrecipe":g("cube_lvrecipe",0x258),
                  "it_type":g("iteminfo_type",0x34),"it_grade":g("iteminfo_grade",0x38),
                  "it_synth":g("iteminfo_synth",0x48),"it_level":g("iteminfo_level",0x6C)}
        self.SYNTH_TYPE_OFF=self.OFF["cubetype"]; self.SYNTH_GRADE_OFF=self.OFF["grade"]; self.SYNTH_LVRECIPE_OFF=self.OFF["lvrecipe"]
    def _resolve_cube_slot(self):
        """cube_slot = RVA do static que segura o singleton uw.Cube. NAO vem do dump (endereco de
        runtime), entao resolve AO VIVO desmontando ilo: 'mov rXX,[rip+disp]' (o static) seguido de
        'mov rXX,[rXX+0xb8]' (o model). Auto-update: funciona em qualquer build."""
        ilo=self.sym.get("ilo")
        if not ilo or not self.pm: return None
        try:
            import capstone
            cs=capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
            code=self.rb(self.base+ilo,0x160)
            if not code: return None
            last=None
            for ins in cs.disasm(code, self.base+ilo):
                op=ins.op_str
                if ins.mnemonic=="mov" and not op.split(",")[0].strip().endswith("]") and "[rip" in op.split(",")[-1]:
                    m=re.search(r'\[rip \+ (0x[0-9a-fA-F]+)\]',op)
                    if m: last=(ins.address+ins.size+int(m.group(1),16))-self.base
                elif ins.mnemonic=="mov" and "+ 0xb8]" in op and last is not None:
                    return last
        except Exception: pass
        return None
    def _proc_alive(self):
        """True se o processo attachado ainda esta vivo. Detecta o jogo reiniciado (handle velho)."""
        try:
            if not self.pm: return False
            r=ctypes.windll.kernel32.WaitForSingleObject(int(self.pm.process_handle),0)
            return r==0x102   # SO WAIT_TIMEOUT=vivo. 0(saiu) ou erro(handle invalido/relaunch) -> reconecta
        except Exception:
            return False
    def launch_game(self):
        try: subprocess.Popen('cmd /c start "" "steam://rungameid/%s"'%STEAM_APPID,shell=True)
        except Exception:
            try: subprocess.Popen([EXE])
            except Exception: pass
    # ---- POPUP "OFFLINE REWARDS": acha o botao Close por OCR e clica com o MOUSE REAL ----
    # A janela do jogo e WS_EX_LAYERED|WS_EX_TRANSPARENT (ex-style 0x80028) = CLICK-THROUGH:
    #  - WindowFromPoint NUNCA devolve o jogo (atravessa p/ a janela de baixo) -> nao serve de gate;
    #  - PostMessage(WM_LBUTTON*) e IGNORADO (o jogo le mouse por RAW INPUT) -> so mouse_event funciona;
    #  - ImageGrab pegaria o que estiver POR CIMA -> a captura tem que ser PrintWindow (pega o
    #    conteudo da janela mesmo ela estando atras). Tudo isso foi medido ao vivo.
    def _game_hwnd(self):
        """hwnd da janela principal do jogo (do nosso pid, visivel, com titulo)."""
        u32=ctypes.windll.user32; out=[]
        class R(ctypes.Structure): _fields_=[("l",ctypes.c_long),("t",ctypes.c_long),("r",ctypes.c_long),("b",ctypes.c_long)]
        @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
        def cb(h,_):
            p=ctypes.c_ulong(); u32.GetWindowThreadProcessId(h,ctypes.byref(p))
            if p.value==self.pid and u32.IsWindowVisible(h) and u32.GetWindowTextLengthW(h)>0:
                rc=R(); u32.GetWindowRect(h,ctypes.byref(rc))
                if (rc.r-rc.l)>200 and (rc.b-rc.t)>200: out.append(h)
            return True
        try: u32.EnumWindows(cb,0)
        except Exception: return None
        return out[0] if out else None
    def _win_shot(self, hwnd):
        """(PIL, (ox,oy)) do conteudo da janela via PrintWindow(PW_RENDERFULLCONTENT)."""
        from PIL import Image
        u32=ctypes.windll.user32; gdi=ctypes.windll.gdi32
        class R(ctypes.Structure): _fields_=[("l",ctypes.c_long),("t",ctypes.c_long),("r",ctypes.c_long),("b",ctypes.c_long)]
        class BH(ctypes.Structure):
            _fields_=[("biSize",ctypes.c_uint32),("biWidth",ctypes.c_long),("biHeight",ctypes.c_long),
                      ("biPlanes",ctypes.c_uint16),("biBitCount",ctypes.c_uint16),("biCompression",ctypes.c_uint32),
                      ("biSizeImage",ctypes.c_uint32),("biXPelsPerMeter",ctypes.c_long),("biYPelsPerMeter",ctypes.c_long),
                      ("biClrUsed",ctypes.c_uint32),("biClrImportant",ctypes.c_uint32)]
        class BI(ctypes.Structure): _fields_=[("bmiHeader",BH),("bmiColors",ctypes.c_uint32*3)]
        rc=R(); u32.GetWindowRect(hwnd,ctypes.byref(rc)); w,h=rc.r-rc.l, rc.b-rc.t
        if w<=0 or h<=0: return None,None
        hdc=u32.GetWindowDC(hwnd); mdc=gdi.CreateCompatibleDC(hdc)
        bmp=gdi.CreateCompatibleBitmap(hdc,w,h); gdi.SelectObject(mdc,bmp)
        u32.PrintWindow(hwnd,mdc,2)                                  # 2 = PW_RENDERFULLCONTENT
        bi=BI(); bi.bmiHeader.biSize=ctypes.sizeof(BH); bi.bmiHeader.biWidth=w
        bi.bmiHeader.biHeight=-h; bi.bmiHeader.biPlanes=1; bi.bmiHeader.biBitCount=32
        buf=ctypes.create_string_buffer(w*h*4); gdi.GetDIBits(mdc,bmp,0,h,buf,ctypes.byref(bi),0)
        gdi.DeleteObject(bmp); gdi.DeleteDC(mdc); u32.ReleaseDC(hwnd,hdc)
        return Image.frombuffer("RGBA",(w,h),buf,"raw","BGRA",0,1), (rc.l,rc.t)
    def _ocr_lines(self, pil, sc=2.5):
        """[(texto,(x0,y0,x1,y1))] via OCR nativo do Windows. Amplia sc x antes (a fonte do jogo e
        pequena demais no 1:1) e devolve as caixas ja no espaco da imagem original."""
        import numpy as np, asyncio
        from winsdk.windows.media.ocr import OcrEngine
        from winsdk.windows.graphics.imaging import SoftwareBitmap, BitmapPixelFormat
        from winsdk.windows.storage.streams import DataWriter
        if not getattr(self,"_ocr_loop",None):
            self._ocr_loop=asyncio.new_event_loop(); self._ocr_eng=OcrEngine.try_create_from_user_profile_languages()
        if not self._ocr_eng: return []
        big=pil.resize((int(pil.size[0]*sc),int(pil.size[1]*sc))).convert("RGBA")
        a=np.asarray(big,dtype=np.uint8); h,w=a.shape[:2]
        dw=DataWriter(); dw.write_bytes(a[:,:,[2,1,0,3]].tobytes()); b=dw.detach_buffer()
        sb=SoftwareBitmap.create_copy_from_buffer(b,BitmapPixelFormat.BGRA8,w,h)
        res=self._ocr_loop.run_until_complete(self._ocr_eng.recognize_async(sb))
        out=[]
        for ln in res.lines:
            xs=[q.bounding_rect.x for q in ln.words]; ys=[q.bounding_rect.y for q in ln.words]
            xe=[q.bounding_rect.x+q.bounding_rect.width for q in ln.words]
            ye=[q.bounding_rect.y+q.bounding_rect.height for q in ln.words]
            out.append((ln.text,(min(xs)/sc,min(ys)/sc,max(xe)/sc,max(ye)/sc)))
        return out
    def _click_real(self, sx, sy):
        """Clique REAL (mouse_event = raw input, unico que o jogo click-through enxerga).
        Devolve o mouse pra onde estava."""
        u32=ctypes.windll.user32
        class P(ctypes.Structure): _fields_=[("x",ctypes.c_long),("y",ctypes.c_long)]
        old=P(); u32.GetCursorPos(ctypes.byref(old))
        u32.SetCursorPos(int(sx),int(sy)); time.sleep(0.08)
        u32.mouse_event(0x0002,0,0,0,0); time.sleep(0.08); u32.mouse_event(0x0004,0,0,0,0)
        time.sleep(0.1); u32.SetCursorPos(old.x,old.y)
    def find_close_button(self):
        """(x,y) na tela do botao 'Close' do OFFLINE REWARDS, ou None. So devolve se CONFIRMAR o
        popup (offline/last login/reward) — evita clicar em qualquer 'Close' solto."""
        try:
            hw=self._game_hwnd()
            if not hw: return None
            img,org=self._win_shot(hw)
            if img is None: return None
            lines=self._ocr_lines(img)
            if not lines: return None
            txt=" | ".join(t.lower() for t,_ in lines)
            if not any(k in txt for k in ("offline","last login","reward")): return None   # nao e o popup
            for t,(x0,y0,x1,y1) in lines:
                if "close" in t.lower():
                    return org[0]+int((x0+x1)/2), org[1]+int((y0+y1)/2)
        except Exception: pass
        return None
    def close_offline_popup(self, window=120):
        """Procura o popup OFFLINE REWARDS por ate 'window' s e fecha clicando no Close.
        (Aparece ~13s depois do jogo abrir e trava o jogo ate fechar.) True se fechou."""
        t0=time.time()
        while time.time()-t0 < window:
            if not (self.pm and self._proc_alive()): return False
            pos=self.find_close_button()
            if pos:
                self.log("popup OFFLINE REWARDS detectado — fechando (clique em %d,%d)"%pos)
                self._click_real(*pos); time.sleep(1.5)
                if not self.find_close_button():
                    self.log("popup fechado ✔"); return True
                self._click_real(*pos); time.sleep(1.5)          # 2a tentativa
                return not self.find_close_button()
            time.sleep(2)
        return False
    # ---- WATCHDOG: mantem o jogo aberto (se cair, reabre pela Steam) ----
    def apply_watchdog(self):
        """Sobe a thread do watchdog se o usuario ligou. Precisa ser chamado FORA do tick(), porque o
        tick retorna cedo quando o jogo esta fechado — que e exatamente quando o watchdog age."""
        if not self.want.get("watchdog"): return
        if self._wd_thr and self._wd_thr.is_alive(): return
        self._wd_thr=threading.Thread(target=self._watchdog_loop,daemon=True); self._wd_thr.start()
    def _watchdog_loop(self):
        """AUTO-RESTART. Quando o jogo FECHA por completo: reabre na hora, com START LIMPO (nada aplicado
        no boot), fecha o botao Close do popup (tolerante a erro), le o estagio que o usuario esta, RELIGA
        todas as opcoes e REENTRA no estagio. A config do usuario fica SALVA no want o tempo todo; o
        _wd_hold so impede o tick() de aplicar durante o boot (por isso o jogo inicia limpo)."""
        STARTUP=120               # ate ~4min esperando o jogo subir
        last_stage=None
        while self.want.get("watchdog"):
            try:
                if self.pm and self._proc_alive():
                    self._wd_hold=False                           # jogo vivo: opera normal
                    try:
                        cur=self.stage_progress()[1]              # rastreia o estagio atual (fallback p/ o restart)
                        if cur: last_stage=cur
                    except Exception: pass
                    time.sleep(2); continue
                # ===================== JOGO FECHOU =====================
                self._wd_hold=True                                # 1) DESLIGA tudo p/ start limpo (config segue salva no want)
                self.log("watchdog: jogo fechou — start limpo (config salva, nada aplicado no boot)")
                gone=0                                            # 2) espera o exe SUMIR TOTALMENTE, ai reabre na hora
                while self._proc_alive():
                    if not self.want.get("watchdog"): self._wd_hold=False; return
                    time.sleep(0.5); gone+=1
                    if gone>240: break                            # trava de seguranca (~2min)
                time.sleep(1)                                     # garante que sumiu de vez
                if not self.want.get("watchdog"): self._wd_hold=False; return
                self.log("watchdog: exe sumiu totalmente — abrindo o jogo")
                self.launch_game()
                up=False                                          # 3) espera o jogo SUBIR (o tick() re-attacha)
                for _ in range(STARTUP):
                    if not self.want.get("watchdog"): self._wd_hold=False; return
                    time.sleep(2)
                    if self.pm and self._proc_alive(): up=True; break
                if not up:
                    self.log("watchdog: jogo nao subiu — tentando de novo"); self._wd_hold=False; continue
                time.sleep(3)                                     # deixa carregar
                try: self.close_offline_popup(window=120)         # 4) fecha o Close do popup — TOLERANTE (se der erro, prossegue)
                except Exception as e: self.log("watchdog: fechar popup deu erro (%s) — prosseguindo"%e)
                try: cur=self.stage_progress()[1] or last_stage   # 5) le o estagio que o usuario esta (save; fallback = ultimo vivo)
                except Exception: cur=last_stage
                self._wd_hold=False                               # 6) RELIGA tudo (libera o tick -> re-aplica ACTk/God/stats/automacoes do want)
                self.log("watchdog: religando todas as opcoes")
                time.sleep(3)                                     # deixa o tick re-aplicar e instalar o dispatcher
                if cur:                                           # 7) REENTRA no estagio com tudo ligado
                    try:
                        if not self.abx: self._install_dispatch() # garante a main-thread p/ entrar
                        tbl=self.stage_table() or {}
                        is_boss=(tbl.get(cur,{}).get("type")==1)
                        ok=self.enter_boss(cur) if is_boss else self.goto_stage(cur)
                        self.log("watchdog: reentrou no estagio %s (%s)=%s"%(cur,"boss" if is_boss else "normal",ok))
                    except Exception as e:
                        self.log("watchdog: reentrar no estagio falhou (%s)"%e)
            except Exception:
                self._wd_hold=False
                time.sleep(3)
        self._wd_hold=False                                       # watchdog desligado -> libera o tick
    # ---- mem helpers ----
    def rb(self,a,n):
        try: return self.pm.read_bytes(a,n)
        except Exception: return None
    def wb(self,a,b):
        try: self.pm.write_bytes(a,b,len(b)); return True
        except Exception: return False
    def u64(self,a):
        d=self.rb(a,8); return struct.unpack("<Q",d)[0] if d else None
    def u32(self,a):
        d=self.rb(a,4); return struct.unpack("<i",d)[0] if d else None
    def vptr(self,p): return p is not None and 0x10000<p<0x7fffffffffff
    def aob(self,pattern,key=None,find_all=False):
        if key and key in self.cache: return self.cache[key]
        pb,mk=parse_aob(pattern); pl=len(pb); CH=0x400000; off=0; hits=[]
        while off<self.size:
            n=min(CH+pl,self.size-off); d=self.rb(self.base+off,n)
            if d:
                i=0
                while True:
                    j=d.find(pb[0:1],i)
                    if j<0 or j>len(d)-pl: break
                    if all(((not mk[k]) or d[j+k]==pb[k]) for k in range(pl)):
                        a=self.base+off+j
                        if not find_all:
                            if key is not None: self.cache[key]=a
                            return a
                        hits.append(a)
                    i=j+1
            off+=CH
        if find_all:
            if key is not None: self.cache[key]=hits
            return hits
        return None
    # ---- cheats ----
    def apply_actk(self):
        orig=self.cache.setdefault("actk_orig",{})
        for rva in self.sym.get("ynj",[]):
            a=self.base+rva; cur=self.rb(a,1)
            if not cur: continue
            if self.want["actk"]:
                if cur!=b'\xC3': orig[rva]=cur; self.wb(a,b'\xC3')
            elif cur==b'\xC3' and rva in orig:
                self.wb(a,orig[rva])
    def apply_god(self):
        a=self.aob(AOB_GODMODE,"god")
        if not a: return
        cur=self.rb(a,1)
        if self.want["god"]:
            if cur and cur!=b'\xC3': self.wb(a,b'\xC3')
        elif cur==b'\xC3':
            self.wb(a,b'\x57')     # restaura prologo (push rdi)
    def alloc_cave(self,near):
        k=ctypes.windll.kernel32
        k.VirtualAllocEx.restype=ctypes.c_void_p
        k.VirtualAllocEx.argtypes=[wintypes.HANDLE,ctypes.c_void_p,ctypes.c_size_t,wintypes.DWORD,wintypes.DWORD]
        k.VirtualQueryEx.restype=ctypes.c_size_t
        h=self.pm.process_handle; LIM=0x7ff00000
        def tryat(addr):
            a=k.VirtualAllocEx(h,ctypes.c_void_p(addr),0x1000,0x3000,0x40)
            if a and abs(a-near)<LIM: return a
            if a: k.VirtualFreeEx(h,ctypes.c_void_p(a),0,0x8000)
            return None
        for o in [0x7000000,0x7200000,0x7400000,0x7800000,0x8000000,0x9000000,0xA000000,-0x2000000,-0x4000000,-0x8000000]:
            a=tryat(near+o)                                   # tentativa rapida (offsets fixos)
            if a: return a
        # ASLR: offsets fixos podem cair em memoria reservada -> escaneia regioes LIVRES via VirtualQueryEx
        class MBI(ctypes.Structure):
            _fields_=[("BaseAddress",ctypes.c_ulonglong),("AllocationBase",ctypes.c_ulonglong),
                      ("AllocationProtect",wintypes.DWORD),("__a1",wintypes.DWORD),
                      ("RegionSize",ctypes.c_ulonglong),("State",wintypes.DWORD),
                      ("Protect",wintypes.DWORD),("Type",wintypes.DWORD),("__a2",wintypes.DWORD)]
        mbi=MBI(); GRAN=0x10000; addr=max(0x10000, near-LIM); hi=near+LIM
        while addr < hi:
            if k.VirtualQueryEx(h,ctypes.c_void_p(addr),ctypes.byref(mbi),ctypes.sizeof(mbi))==0: break
            base=mbi.BaseAddress; size=mbi.RegionSize
            if not size: break
            if mbi.State==0x10000 and size>=0x2000:           # MEM_FREE
                cand=(base+GRAN-1)&~(GRAN-1)
                if cand+0x1000<=base+size and abs(cand-near)<LIM:
                    a=k.VirtualAllocEx(h,ctypes.c_void_p(cand),0x1000,0x3000,0x40)
                    if a and abs(a-near)<LIM: return a
                    if a: k.VirtualFreeEx(h,ctypes.c_void_p(a),0,0x8000)
            addr=base+size
        return None
    def apply_hitkill(self):
        gra_rva=self.sym.get("gra")
        if not gra_rva: return
        GRA=self.base+gra_rva; g=self.rb(GRA,5)
        if not g: return
        if not self.want["hitkill"]:
            if g[0]==0xE9: self.wb(GRA,GRA_ORIG)   # desliga: restaura prologo
            return
        if g[0]==0xE9: return                       # ja instalado
        if g!=GRA_ORIG: return                       # prologo inesperado -> nao arrisca
        cave=self.alloc_cave(GRA)
        if not cave: return
        cc=b'\xC7\x42\x08'+struct.pack('<f',1e18)+GRA_ORIG
        cc+=b'\xE9'+struct.pack('<i',(GRA+5)-(cave+len(cc)+5))
        self.wb(cave,cc); self.wb(GRA,b'\xE9'+struct.pack('<i',cave-(GRA+5)))
        self.log("HITKILL detour @ %#x"%cave)
    def _stats_obj(self):
        slot=self.aob(AOB_PSTAT,"pstat")
        if not slot: return None
        rel=self.u32(slot+3)
        if rel is None: return None
        p=self.u64(slot+7+rel)
        for off in PSTAT_CHAIN:
            if not self.vptr(p): return None
            p=self.u64(p+off)
        return p if self.vptr(p) else None
    def read_stats(self):
        with self.lock:
            p=self._stats_obj()
            if not p: return {}
            out={}
            for name,(off,typ) in STATS.items():
                d=self.rb(p+off, 8 if typ=='d' else 4)
                if d: out[name]=struct.unpack('<d' if typ=='d' else '<f', d)[0]
            return out
    def apply_stats(self):
        merged=dict(self.stats); merged.update(self.speed_stats)   # speed sobrepoe manual
        if not merged: return
        p=self._stats_obj()
        if not p: return
        for name,val in merged.items():
            if name not in STATS: continue
            off,typ=STATS[name]
            self.wb(p+off, struct.pack('<d',val) if typ=='d' else struct.pack('<f',val))
    def _stage_obj(self):
        if "stage_slots" not in self.cache:
            self.cache["stage_slots"]=[]
            for m in self.aob(AOB_STAGE,"stage_aob",find_all=True):
                rel=self.u32(m+3)
                if rel is not None: self.cache["stage_slots"].append(m+7+rel)
        def sd_of(slot):
            p=self.u64(slot)
            for off in STAGE_CHAIN:
                if not self.vptr(p): return None
                p=self.u64(p+off)
            return p if self.vptr(p) else None
        def sane(sd):
            v=[self.u32(sd+o) for o in (0x48,0x4C,0x50,0x54,0x58)]
            if any(x is None for x in v): return False
            a,s,l,w,mo=v
            return 1<=a<=12 and 1<=s<=40 and 1<=l<=300 and 1<=w<=80 and 1<=mo<=400
        if self.cache.get("stage_slot"):
            sd=sd_of(self.cache["stage_slot"])
            if sd and sane(sd): return sd
            self.cache["stage_slot"]=None
        for slot in self.cache["stage_slots"]:
            sd=sd_of(slot)
            if sd and sane(sd): self.cache["stage_slot"]=slot; return sd
        return None
    def read_stage(self):
        with self.lock:
            sd=self._stage_obj()
            if not sd: return {}
            return {k:self.u32(sd+o) for k,o in STAGE_FIELDS.items()}
    def apply_stage(self):
        if not self.stage: return
        sd=self._stage_obj()
        if not sd: return
        for k,val in self.stage.items():
            if k in STAGE_FIELDS: self.wb(sd+STAGE_FIELDS[k], struct.pack('<i',int(val)))
    # ---- inventario ----
    def _mem_regions(self,prots):
        out=[]; addr=0; mbi=MBI()
        while addr<0x7fffffffffff:
            if not _VQ(self.pm.process_handle,ctypes.c_void_p(addr),ctypes.byref(mbi),ctypes.sizeof(mbi)): break
            base=mbi.BaseAddress or 0; size=mbi.RegionSize or 0x1000
            if mbi.State==0x1000 and mbi.Protect in prots and 0<size<0x8000000: out.append((base,size))
            addr=base+size
        return out
    def _clsname(self,k):
        try:
            n=self.u64(k+0x10); return self.pm.read_bytes(n,32).split(b"\x00")[0].decode("latin1","ignore")
        except Exception: return ""
    def _valid_bau(self,inst):
        po=self.sym.get("inv_psd_off",INV_PSD_OFF); lo=self.sym.get("inv_list_off",INV_LIST_OFF)
        psd=self.u64(inst+po)
        if not self.vptr(psd): return False
        lst=self.u64(psd+lo)
        if not self.vptr(lst): return False
        sz=self.u32(lst+0x18)
        return sz is not None and 0<=sz<500000
    def _resolve_bau(self):
        """Acha a instancia do singleton que segura PlayerSaveData (via TypeInfo se exportado, senao scan)."""
        ti=self.sym.get("bau_ti")
        if ti:
            k=self.u64(self.base+ti); sf=self.u64(k+STATIC_FIELDS_OFF) if k else None
            bau=self.u64(sf) if sf else None
            if self.vptr(bau) and self._valid_bau(bau): return bau
        # cache runtime
        c=self.cache.get("bau_inst")
        if c and self._valid_bau(c): return c
        if self.cache.get("bau_nf"): return None            # ja tentou e falhou
        # via classe concreta (X_TypeInfo) -> klass -> scan da instancia (rapido, valida a chain)
        kti=self.sym.get("inv_klass_ti")
        if kti:
            klass=self.u64(self.base+kti)
            if self.vptr(klass):
                kp=struct.pack("<Q",klass)
                for base,size in self._mem_regions((0x04,0x40)):
                    d=self.rb(base,size)
                    if not d: continue
                    j=d.find(kp)
                    while j>=0:
                        a=base+j
                        if self._valid_bau(a): self.cache["bau_inst"]=a; return a
                        j=d.find(kp,j+8)
        self.cache["bau_nf"]=True
        return None
    def read_inventory(self):
        """Counter{itemKey:qtd} de TODOS os itens (inv+baus)."""
        with self.lock:
            if (not self.pm or not self._proc_alive()): self.attach()   # jogo reiniciou -> reconecta
            bau=self._resolve_bau()
            if not bau: return None
            po=self.sym.get("inv_psd_off",INV_PSD_OFF); lo=self.sym.get("inv_list_off",INV_LIST_OFF)
            psd=self.u64(bau+po)
            lst=self.u64(psd+lo) if psd else None
            if not lst: return None
            arr=self.u64(lst+0x10); size=self.u32(lst+0x18)
            if not arr or size is None or not (0<=size<500000): return None
            inv=collections.Counter()
            for i in range(size):
                it=self.u64(arr+0x20+i*8)
                if not it: continue
                ik=self.u32(it+ITEMSAVE_KEY_OFF)
                if ik and 100000<=ik<=999999: inv[ik]+=1
            return inv

    # ============================= RUNES (tabela Runes) — 100% client-side =============================
    def _il2str(self, ptr):
        """System.String IL2CPP (len@0x10, chars UTF-16LE @0x14)."""
        if not ptr or ptr < 0x10000: return None
        ln = self.u32(ptr + 0x10)
        if ln is None or ln <= 0 or ln > 300: return None
        raw = self.rb(ptr + 0x14, ln * 2)
        if not raw: return None
        try:
            s = raw.decode('utf-16-le', 'replace')
            return s if all(31 < ord(c) < 0x3000 or c in ' /-_.' for c in s) else None
        except Exception:
            return None

    def read_rune_defs(self, force=False):
        """{key:{name,max,next:[keys],icon}} das RuneInfoData (scan da klass). Cacheado (dado estatico)."""
        if not force and getattr(self, "_rune_defs", None): return self._rune_defs
        with self.lock:
            if (not self.pm or not self._proc_alive()): self.attach()
            klass = self._klass_by_name("TaskbarHero.Data", "RuneInfoData")
            if not klass: return None
            kp = struct.pack("<Q", klass); defs = {}
            for base, size in self._mem_regions((0x04, 0x40)):
                d = self.rb(base, size)
                if not d: continue
                j = d.find(kp)
                while j >= 0:
                    if j % 8 == 0:
                        a = base + j
                        key = self.u32(a + 0x30); mx = self.u32(a + 0x34)
                        nm = self._il2str(self.u64(a + 0x38))
                        if nm and key is not None and 0 <= key < 20000000:
                            nxt = []
                            for fld in (0x40, 0x48):
                                s = self._il2str(self.u64(a + fld))
                                if s: nxt += [int(t) for t in s.split() if t.isdigit()]
                            ic = self._il2str(self.u64(a + 0x58)) or ""
                            defs[key] = {"name": nm.replace("RuneName_", ""), "max": (mx or 1), "next": nxt, "icon": ic}
                    j = d.find(kp, j + 8)
            if defs: self._rune_defs = defs
            return defs or None

    def _player_psd(self):
        """(PSD, rune_off) com lista de runa VALIDA — robusto contra bau-fantasma pos-reload."""
        po = self.sym.get("inv_psd_off", INV_PSD_OFF)
        ro = self.sym.get("PlayerSaveData.RuneSaveData", RUNE_LIST_OFF)
        bau = self._resolve_bau()
        if bau:
            psd = self.u64(bau + po)
            if self.vptr(psd):
                lst = self.u64(psd + ro); cnt = self.u32(lst + 0x18) if lst else None
                if cnt and 50 < cnt < 2000: return psd, ro
        klass = None
        kti = self.sym.get("inv_klass_ti")
        if kti: klass = self.u64(self.base + kti)
        if not klass and bau: klass = self.u64(bau)
        if not klass: return None, ro
        kp = struct.pack("<Q", klass)
        for base, size in self._mem_regions((0x04, 0x40)):
            d = self.rb(base, size)
            if not d: continue
            j = d.find(kp)
            while j >= 0:
                if j % 8 == 0:
                    psd = self.u64(base + j + po)
                    if self.vptr(psd):
                        lst = self.u64(psd + ro); cnt = self.u32(lst + 0x18) if lst else None
                        if cnt and 50 < cnt < 2000: return psd, ro
                j = d.find(kp, j + 8)
        return None, ro

    def read_runes(self):
        """{key:level} do RuneSaveData. None se desatachado/nao resolveu."""
        with self.lock:
            if (not self.pm or not self._proc_alive()): self.attach()
            psd, ro = self._player_psd()
            if not psd: return None
            lst = self.u64(psd + ro)
            arr = self.u64(lst + 0x10) if lst else None; size = self.u32(lst + 0x18) if lst else None
            if not arr or size is None or not (0 <= size < 100000): return None
            out = {}
            for i in range(size):
                r = self.u64(arr + 0x20 + i * 8)
                if r: out[self.u32(r + 0x10)] = self.u32(r + 0x14)
            return out

    def set_rune(self, key, level):
        """Seta o Level de UMA runa (client-side). CLAMP DURO [0..max] — NUNCA passa do teto
        (over-max crasha o load: NRE em RuneNode.mav). Retorna o nivel aplicado, ou None."""
        defs = self.read_rune_defs() or {}
        mx = int(defs.get(key, {}).get("max", 1) or 1)
        level = max(0, min(int(level), mx))
        with self.lock:
            psd, ro = self._player_psd()
            if not psd: return None
            lst = self.u64(psd + ro)
            arr = self.u64(lst + 0x10) if lst else None; size = self.u32(lst + 0x18) if lst else None
            if not arr or not size: return None
            for i in range(size):
                r = self.u64(arr + 0x20 + i * 8)
                if r and self.u32(r + 0x10) == key:
                    self.wb(r + 0x14, struct.pack('<i', level)); return level
            return None

    def unlock_runes(self, keys=None, to_max=True):
        """Desbloqueia varias numa passada. keys=None -> TODAS. to_max -> nivel maximo (senao 1).
        Retorna quantas mudaram. Clamp por-runa (nunca passa do teto)."""
        defs = self.read_rune_defs()
        if defs is None: return 0
        keyset = set(keys) if keys is not None else set(defs.keys())
        with self.lock:
            psd, ro = self._player_psd()
            if not psd: return 0
            lst = self.u64(psd + ro)
            arr = self.u64(lst + 0x10) if lst else None; size = self.u32(lst + 0x18) if lst else None
            if not arr or not size: return 0
            n = 0
            for i in range(size):
                r = self.u64(arr + 0x20 + i * 8)
                if not r: continue
                k = self.u32(r + 0x10)
                if k in keyset:
                    mx = int(defs.get(k, {}).get("max", 1) or 1)
                    tgt = max(0, min(mx if to_max else 1, mx))
                    if self.u32(r + 0x14) != tgt:
                        self.wb(r + 0x14, struct.pack('<i', tgt)); n += 1
            return n

    # ---- SPEEDHACK de relogio (hook em QueryPerformanceCounter, estilo Cheat Engine) ----
    def _qpc_addr(self):
        k=ctypes.windll.kernel32
        k.GetProcAddress.restype=ctypes.c_void_p; k.GetProcAddress.argtypes=[ctypes.c_void_p,ctypes.c_char_p]
        k.GetModuleHandleA.restype=ctypes.c_void_p
        h=k.GetModuleHandleA(b"kernelbase.dll") or k.GetModuleHandleA(b"kernel32.dll")
        return k.GetProcAddress(ctypes.c_void_p(h),b"QueryPerformanceCounter")  # msmo endereco no jogo (DLL de sistema)
    def _my_qpc(self):
        v=ctypes.c_int64(); ctypes.windll.kernel32.QueryPerformanceCounter(ctypes.byref(v)); return v.value
    def _sh_code(self,cave,tramp,data,stolen):
        # cave: chama o QPC original (tramp), le real, e escala: fake=fakeBase+(real-realBase)*speed
        c=bytearray(b'\x51\x48\x83\xEC\x20'); cp=cave+len(c); c+=b'\xE8'+struct.pack("<i",tramp-(cp+5))
        c+=b'\x48\x83\xC4\x20\x59\x48\x8B\x01\x49\xBA'+struct.pack("<Q",data)
        c+=b'\x41\x80\x7A\x10\x00'; jp=len(c); c+=b'\x75\x00'
        c+=b'\x49\x89\x42\x00\x49\x89\x42\x08\x41\xC6\x42\x10\x01'; c[jp+1]=len(c)-(jp+2)
        c+=b'\x49\x2B\x42\x00\xF2\x48\x0F\x2A\xC0\xF2\x41\x0F\x59\x42\x18\xF2\x48\x0F\x2D\xC0\x49\x03\x42\x08\x48\x89\x01\xB8\x01\x00\x00\x00\xC3'
        return bytes(c)
    def _vprotectex(self,addr,n,prot):
        k=ctypes.windll.kernel32
        k.VirtualProtectEx.argtypes=[wintypes.HANDLE,ctypes.c_void_p,ctypes.c_size_t,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD)]
        old=wintypes.DWORD(); k.VirtualProtectEx(self.pm.process_handle,ctypes.c_void_p(addr),n,prot,ctypes.byref(old)); return old
    def _suspend_all(self):
        # suspende as threads do jogo pra patchar a QPC com seguranca (evita escrita rasgada -> crash)
        k=ctypes.windll.kernel32
        class TE32(ctypes.Structure):
            _fields_=[("dwSize",wintypes.DWORD),("cntUsage",wintypes.DWORD),("th32ThreadID",wintypes.DWORD),
                      ("th32OwnerProcessID",wintypes.DWORD),("tpBasePri",ctypes.c_long),("tpDeltaPri",ctypes.c_long),("dwFlags",wintypes.DWORD)]
        k.CreateToolhelp32Snapshot.restype=wintypes.HANDLE; k.OpenThread.restype=wintypes.HANDLE
        snap=k.CreateToolhelp32Snapshot(0x4,0)
        hs=[]
        if snap and snap!=-1:
            te=TE32(); te.dwSize=ctypes.sizeof(te)
            ok=k.Thread32First(snap,ctypes.byref(te))
            while ok:
                if te.th32OwnerProcessID==self.pid:
                    h=k.OpenThread(0x0002,False,te.th32ThreadID)   # THREAD_SUSPEND_RESUME
                    if h: k.SuspendThread(h); hs.append(h)
                ok=k.Thread32Next(snap,ctypes.byref(te))
            k.CloseHandle(snap)
        return hs
    def _resume_all(self,hs):
        k=ctypes.windll.kernel32
        for h in hs:
            try: k.ResumeThread(h); k.CloseHandle(h)
            except Exception: pass
    def _install_speedhack(self,mult):
        try:
            qpc=self._qpc_addr()
            if not qpc: return False
            stolen=self.rb(qpc,5)
            if not stolen or len(stolen)!=5: return False
            cave=self.alloc_cave(qpc)
            if not cave: self.log("speedhack: cave failed"); return False
            DATA=cave+0x100; TRAMP=cave+0x80
            self.wb(cave,self._sh_code(cave,TRAMP,DATA,stolen))
            self.wb(TRAMP,bytes(stolen)+b'\xE9'+struct.pack("<i",(qpc+5)-(TRAMP+10)))
            self.wb(DATA,struct.pack("<qqQd",0,0,0,float(mult)))
            old=self._vprotectex(qpc,5,0x40)
            hs=self._suspend_all()                    # congela o jogo pra patchar a QPC
            try: self.wb(qpc,b'\xE9'+struct.pack("<i",cave-(qpc+5)))
            finally: self._resume_all(hs)
            self._vprotectex(qpc,5,old.value)
            f=ctypes.c_int64(); ctypes.windll.kernel32.QueryPerformanceFrequency(ctypes.byref(f))
            self.sh={"cave":cave,"data":DATA,"stolen":bytes(stolen),"qpc":qpc,"cur":float(mult)}
            self.log("SPEEDHACK installed (x%.2g) @ %#x"%(mult,cave)); return True
        except Exception as e:
            self.log("speedhack failed: %s"%e); return False
    def _rebase_speedhack(self,newmult):
        try:
            DATA=self.sh["data"]; d=self.rb(DATA,32)
            if not d: return
            rb,fb,inited,sp=struct.unpack("<qqQd",d)
            if not inited:                       # jogo ainda nao chamou QPC -> so troca o speed
                self.wb(DATA+24,struct.pack("<d",float(newmult))); self.sh["cur"]=float(newmult); return
            real=self._my_qpc(); curfake=fb+int((real-rb)*sp)   # rebase suave (sem pulo)
            self.wb(DATA+0,struct.pack("<q",real)); self.wb(DATA+8,struct.pack("<q",curfake))
            self.wb(DATA+24,struct.pack("<d",float(newmult))); self.sh["cur"]=float(newmult)
        except Exception as e:
            self.log("rebase speedhack: %s"%e)
    def _remove_speedhack(self):
        if not self.sh: return
        try:
            self._rebase_speedhack(1.0)                    # neutraliza antes de tirar (minimiza pulo)
            old=self._vprotectex(self.sh["qpc"],5,0x40)
            hs=self._suspend_all()
            try: self.wb(self.sh["qpc"],self.sh["stolen"])
            finally: self._resume_all(hs)
            self._vprotectex(self.sh["qpc"],5,old.value)
            self.log("speedhack removed (QPC restored)")
        except Exception: pass
        self.sh=None
    def apply_speedhack(self):
        want=self.sh_mult if (self.sh_mult and self.sh_mult>0) else 1.0
        if not self.sh:
            if want!=1.0: self._install_speedhack(want)   # 1.0 = nao precisa instalar
        elif abs(want-self.sh["cur"])>1e-9:
            self._rebase_speedhack(want)                  # off = rebase pra 1.0 (mantem hook neutro)

    # ---- AUTO-CAIXA (abre caixas assim que surgem, via memoria) ----
    # Dispatcher: hook em InputManager.Update (roda todo frame na MAIN-THREAD do jogo).
    # Python detecta os StageBox e seta alvo+flag; o hook chama StageBox.llx(this,0) =
    # exatamente um clique esquerdo (o open async precisa da main-thread, senao crasha).
    # llx auto-checa iyf()/contagem/busy, entao abrir so acontece quando ha caixa.
    def _export(self, name):
        """Resolve um export do GameAssembly.dll por NOME (estavel entre builds)."""
        try:
            b=self.base; lfa=self.u32(b+0x3C); pe=b+lfa
            exp_rva=self.u32(pe+0x88)                       # DataDirectory[0] (export) RVA (PE32+)
            if not exp_rva: return None
            ed=b+exp_rva
            nfun=self.u32(ed+0x18); afun=self.u32(ed+0x1C); anam=self.u32(ed+0x20); aord=self.u32(ed+0x24)
            tgt=name.encode()
            for i in range(nfun):
                nrva=self.u32(b+anam+i*4)
                s=self.rb(b+nrva,64)
                if s and s.split(b"\x00")[0]==tgt:
                    ordi=struct.unpack("<H",self.rb(b+aord+i*2,2))[0]
                    return b+self.u32(b+afun+ordi*4)
        except Exception: pass
        return None
    def _remote_call(self, func, args):
        """Chama func(args...) numa thread remota, retorna rax. Use SO p/ funcs sem afinidade
        de thread (ex il2cpp_class_from_name, getters puros). NAO usar p/ metodos Unity async (crasha).
        Serializado por _rc_lock (o scratch buffer e compartilhado -> chamadas concorrentes se corrompem)."""
        with self._rc_lock:
            k=ctypes.windll.kernel32
            k.VirtualAllocEx.restype=ctypes.c_void_p; k.CreateRemoteThread.restype=ctypes.c_void_p
            sc=self.cache.get("rc_scratch")
            if not sc:
                k.VirtualAllocEx.argtypes=[wintypes.HANDLE,ctypes.c_void_p,ctypes.c_size_t,wintypes.DWORD,wintypes.DWORD]
                sc=k.VirtualAllocEx(self.pm.process_handle,None,0x1000,0x3000,0x40); self.cache["rc_scratch"]=sc
            if not sc: return None
            RES=sc+0x100; args=(list(args)+[0,0,0,0])[:4]
            c=bytearray(b"\x48\x83\xEC\x38")
            for reg,val in zip((b"\x48\xB9",b"\x48\xBA",b"\x49\xB8",b"\x49\xB9"),args): c+=reg+struct.pack("<Q",val)
            c+=b"\x48\xB8"+struct.pack("<Q",func)+b"\xFF\xD0"
            c+=b"\x48\xB9"+struct.pack("<Q",RES)+b"\x48\x89\x01\x48\x83\xC4\x38\x31\xC0\xC3"
            self.wb(sc,bytes(c)); self.wb(RES,b"\x00"*8)
            k.CreateRemoteThread.argtypes=[wintypes.HANDLE,ctypes.c_void_p,ctypes.c_size_t,ctypes.c_void_p,ctypes.c_void_p,wintypes.DWORD,ctypes.c_void_p]
            th=k.CreateRemoteThread(self.pm.process_handle,None,0,ctypes.c_void_p(sc),None,0,None)
            if not th: return None
            k.WaitForSingleObject(wintypes.HANDLE(th),5000); k.CloseHandle(wintypes.HANDLE(th))
            return self.u64(RES)
    def _iuw_count(self, boxtype):
        """Contagem REAL de caixas abriveis do tipo (iuw, uw.tw). None se offset ausente.
        Getter puro -> seguro via _remote_call (provado). E o unico contador que decrementa ao abrir."""
        iuw=self.sym.get("iuw")
        if not iuw: return None
        try:
            r=self._remote_call(self.base+iuw,[int(boxtype)])
            return (r & 0xffffffff) if r is not None else None
        except Exception: return None
    def _stagebox_klass(self):
        """Il2CppClass* do StageBox via il2cpp_class_from_name (nome estavel). Cacheado."""
        c=self.cache.get("sb_klass")
        if c: return c
        E={n:self._export(n) for n in ("il2cpp_domain_get","il2cpp_domain_get_assemblies","il2cpp_assembly_get_image","il2cpp_class_from_name")}
        if not all(E.values()): return None
        sc=self.cache.get("rc_scratch2")
        if not sc:
            ctypes.windll.kernel32.VirtualAllocEx.restype=ctypes.c_void_p
            sc=ctypes.windll.kernel32.VirtualAllocEx(self.pm.process_handle,None,0x1000,0x3000,0x40); self.cache["rc_scratch2"]=sc
        if not sc: return None
        NS=sc+0x40; NM=sc+0x80; SZ=sc+0xC0
        self.wb(NS,b"TaskbarHero.UI\x00"); self.wb(NM,b"StageBox\x00")
        dom=self._remote_call(E["il2cpp_domain_get"],[])
        asms=self._remote_call(E["il2cpp_domain_get_assemblies"],[dom,SZ]); n=self.u64(SZ) or 0
        for i in range(min(n,200)):
            a=self.u64(asms+i*8)
            if not a: continue
            img=self._remote_call(E["il2cpp_assembly_get_image"],[a])
            kl=self._remote_call(E["il2cpp_class_from_name"],[img,NS,NM])
            if kl: self.cache["sb_klass"]=kl; return kl
        return None
    def _heap_ptr(self, p):
        return bool(p) and 0x10000000000<=p<0x7f0000000000

    def _valid_stagebox(self, a, klass):
        """A StageBox em `a` esta VIVA? O klass ptr sozinho NAO serve: memoria liberada mantem os 8
        primeiros bytes, entao um objeto MORTO passava na checagem e llx nele FECHA O JOGO (medido:
        ACTBOSS morta com m_CachedPtr=0xb1330200 enquanto a viva tinha 0x17841f9a4b0).
        m_CachedPtr (+0x10) = ponteiro nativo do Unity: GameObject destruido -> 0/lixo. E exatamente
        o que o proprio Unity testa no 'obj == null'."""
        if self.u64(a)!=klass: return False
        if not self._heap_ptr(self.u64(a+0x10)): return False              # m_CachedPtr -> DESTRUIDA
        if self.u32(a+0x38) not in (0,1,2): return False                   # EBoxType
        btn=self.u64(a+0x58)
        if not self._heap_ptr(btn) or self.u64(btn)==klass: return False
        hb=self.rb(a+0x128,1); bz=self.rb(a+0xa0,1)
        return bool(hb) and hb[0]<=1 and bool(bz) and bz[0]<=1

    def _find_stageboxes(self):
        """{EBoxType: ptr} das instancias REAIS **e VIVAS** de StageBox. Caixa e UI transiente: o
        jogo destroi/recria (restart, troca de Act) e deixa um cadaver no heap com o klass intacto.
        O cadaver costuma cair num endereco MENOR -> vinha primeiro no scan e ganhava o setdefault
        (era o crash do auto-box: so a ACTBOSS estava morta -> so quebrava as vezes)."""
        klass=self._stagebox_klass()
        if not klass: return {}
        cc=self.cache.get("sb_inst")                                       # so reusa cache se AINDA vivas
        if cc and cc.get("_k")==klass and cc.get("map") and all(self._valid_stagebox(a,klass) for a in cc["map"].values()):
            return cc["map"]
        kp=struct.pack("<Q",klass); found={}
        for base,size in self._mem_regions((0x04,0x40)):
            d=self.rb(base,size)
            if not d: continue
            j=d.find(kp)
            while j>=0:
                if j%8==0 and self._valid_stagebox(base+j, klass):
                    found.setdefault(self.u32(base+j+0x38), base+j)
                j=d.find(kp,j+8)
            if len(found)>=3: break
        self.cache["sb_inst"]={"_k":klass,"map":found}
        return found
    # === DISPATCHER GENERICO (um hook em InputManager.Update, varios comandos main-thread) ===
    # data @cave+0x800: en@0 doFlag@1 inop@2 cmd@4 argP@8 argI@0x10 cnt@0x14
    # cmds: 1=box llx(argP,0) · 2=stash igj(argP=ra,INVENTORY,argI,null,STASH) ·
    #       3=synth ilo(argI) · 4=synth ipu() · 5=synth imx()
    def _dispatch_code(self, cave, back_va, stolen):
        s=self.sym; B=self.base
        LLX=B+s.get("llx",0); IW=B+s.get("iw",0); ILO=B+s.get("ilo",0); IPU=B+s.get("ipu",0); IMX=B+s.get("imx",0); INF=B+s.get("inf",0); ILI=B+s.get("ili",0); IOG=B+s.get("iog",0); IOA=B+s.get("ioa",0); IMA=B+s.get("ima",0); LLM=B+s.get("llm",0); JGD=B+s.get("jgd",0)
        D=cave+0x800; D_EN=D+0; D_DO=D+1; D_INOP=D+2; D_CMD=D+4; D_ARGP=D+8; D_ARGI=D+0x10; D_CNT=D+0x14; D_ARG2=D+0x18; D_REQ=D+0x20
        D_FUNC=D+0x38; D_RET=D+0x40    # cmd12 chama [D_FUNC]; retorno de qualquer cmd cai em D_RET (ex EAddCubeResult do ioa)
        FAKE=cave+0x920      # delegate falso (callback p/ iw): [+0x18]=ret -> invoke volta limpo, sem NRE
        c=bytearray(); imm=lambda r: struct.pack("<Q",r); lab={}; fix=[]
        def jcc(op,l): c.extend(op); fix.append((len(c),l)); c.extend(b"\x00\x00\x00\x00")
        def jmp(l): c.append(0xE9); fix.append((len(c),l)); c.extend(b"\x00\x00\x00\x00")
        def L(n): lab[n]=len(c)
        c+=bytes([0x50,0x51,0x52,0x53,0x41,0x50,0x41,0x51,0x41,0x52,0x41,0x53,0x9C])   # push rax rcx rdx rbx r8-r11; pushfq
        c+=b"\x48\xB8"+imm(D_EN)+b"\x80\x38\x00"; jcc(b"\x0F\x84","skip")
        c+=b"\x48\xB8"+imm(D_DO)+b"\x80\x38\x00"; jcc(b"\x0F\x84","skip")
        c+=b"\xC6\x00\x00"                                            # doFlag=0
        c+=b"\x48\xB8"+imm(D_INOP)+b"\x80\x38\x00"; jcc(b"\x0F\x85","skip")
        c+=b"\x48\xB8"+imm(D_INOP)+b"\xC6\x00\x01"                     # inop=1
        c+=b"\x48\xB8"+imm(D_CMD)+b"\x8B\x00"                          # eax=[cmd]
        # cmd1 box
        c+=b"\x83\xF8\x01"; jcc(b"\x0F\x85","c2")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08\x48\x85\xC9"; jcc(b"\x0F\x84","done")   # rcx=[argP]; je done
        c+=b"\x31\xD2\x4D\x31\xC0\x48\x83\xEC\x20\x48\xB8"+imm(LLX)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")
        # cmd2 stash — iw(ra, &MoveRequest@D_REQ, FAKE)  (fake delegate = callback valido -> sem NRE)
        L("c2"); c+=b"\x83\xF8\x02"; jcc(b"\x0F\x85","c3")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08\x48\x85\xC9"; jcc(b"\x0F\x84","done")   # rcx=[argP]=ra
        c+=b"\x48\xBA"+imm(D_REQ)                                     # rdx=&MoveRequest
        c+=b"\x49\xB8"+imm(FAKE)                                      # r8=fake delegate (nao null)
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(IW)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")
        # cmd3 ilo(argI)
        L("c3"); c+=b"\x83\xF8\x03"; jcc(b"\x0F\x85","c4")
        c+=b"\x48\xB8"+imm(D_ARGI)+b"\x8B\x08\x48\x83\xEC\x20\x48\xB8"+imm(ILO)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")
        # cmd4 ipu()
        L("c4"); c+=b"\x83\xF8\x04"; jcc(b"\x0F\x85","c5")
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(IPU)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")
        # cmd5 imx()
        L("c5"); c+=b"\x83\xF8\x05"; jcc(b"\x0F\x85","c6")
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(IMX)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")
        # cmd6 inf(argP=ux) = seleciona a recipe (seta bery@MODEL+0x140, pra imx rotear pra synthesis)
        L("c6"); c+=b"\x83\xF8\x06"; jcc(b"\x0F\x85","c7")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08\x48\x85\xC9"; jcc(b"\x0F\x84","done")   # rcx=[argP]=ux
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(INF)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")
        # cmd7 ili(argI=grade) = dispara o EVENTO de grade (nao popula sel@0x240 headless; a UI e quem faz)
        L("c7"); c+=b"\x83\xF8\x07"; jcc(b"\x0F\x85","c8")
        c+=b"\x48\xB8"+imm(D_ARGI)+b"\x8B\x08\x48\x83\xEC\x20\x48\xB8"+imm(ILI)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")   # ecx=grade; ili(grade)
        # cmd8 iog(argI=a, argP=CubeInData) = builder da recipe-de-grade -> chama ioi -> POPULA sel@[MODEL+0x240] headless!
        L("c8"); c+=b"\x83\xF8\x08"; jcc(b"\x0F\x85","c9")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x10\x48\x85\xD2"; jcc(b"\x0F\x84","done")   # rdx=[argP]=CubeInData; je done
        c+=b"\x48\xB8"+imm(D_ARGI)+b"\x8B\x08"                                            # ecx=[argI]=a
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(IOG)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")  # iog(a, CubeInData)
        # cmd9 ioa(argI=ESlotType, arg2=index) = ADICIONA item real (do baU/inv) AO CUBO (fluxo legitimo, monta a recipe)
        L("c9"); c+=b"\x83\xF8\x09"; jcc(b"\x0F\x85","c10")
        c+=b"\x48\xB8"+imm(D_ARGI)+b"\x8B\x08"                                            # ecx=[argI]=ESlotType
        c+=b"\x48\xB8"+imm(D_ARG2)+b"\x8B\x10"                                            # edx=[arg2]=index
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(IOA)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")  # ioa(slotType, index)
        # cmd10 ima(argP=ux) = SETA a variante de nivel (bery=ux). ux tier8 = Lv.65~80 -> filtro 65-80 HEADLESS!
        L("c10"); c+=b"\x83\xF8\x0A"; jcc(b"\x0F\x85","c11")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08\x48\x85\xC9"; jcc(b"\x0F\x84","done")   # rcx=[argP]=ux; je done
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(IMA)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")  # ima(ux)
        # cmd11 llm(argP=box) = StageBox.llm() abrir direto (sem as checagens de clique do llx)
        L("c11"); c+=b"\x83\xF8\x0B"; jcc(b"\x0F\x85","c13")   # corrente: ...c11 -> c13 -> c12 -> done
                                                               # (pular daqui direto pro c12 deixaria o
                                                               #  c13 INALCANCAVEL — foi esse o bug)
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08\x48\x85\xC9"; jcc(b"\x0F\x84","done")   # rcx=[argP]=box; je done
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(LLM)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")  # llm()
        # cmd12 CALL GENERICO: [D_FUNC](rcx=[argP], edx=[argI]). Retorno em D_RET. P/ clear do cubo (inl/inm) etc.
        # cmd13 jgd(argP=StageCache, FAKE) = entrar em ACTBOSS pelo caminho do clique (reserva a soulstone).
        # O callback do clique NAO e cosmetico: ele chama jgk (o passo final que carrega a fase). Por isso
        # o painel manda cmd13 E DEPOIS jgk — o par equivale ao clique vanilla (so falta o som).
        L("c13"); c+=b"\x83\xF8\x0D"; jcc(b"\x0F\x85","c12")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08\x48\x85\xC9"; jcc(b"\x0F\x84","done")   # rcx=[argP]=cache; je done
        c+=b"\x48\xBA"+imm(FAKE)                                       # rdx=fake delegate (nao null -> sem NRE)
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(JGD)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")
        L("c12"); c+=b"\x83\xF8\x0C"; jcc(b"\x0F\x85","done")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08"                     # rcx=[argP]
        c+=b"\x48\xB8"+imm(D_ARGI)+b"\x8B\x10"                         # edx=[argI]
        c+=b"\x48\xB8"+imm(D_FUNC)+b"\x48\x8B\x00"                     # rax=[D_FUNC]
        c+=b"\x48\x83\xEC\x20\xFF\xD0\x48\x83\xC4\x20"; jmp("done")    # call rax
        L("done")
        c+=b"\x49\xBB"+imm(D_RET)+b"\x49\x89\x03"                      # r11=D_RET; [r11]=rax (captura retorno, ex EAddCubeResult)
        c+=b"\x48\xB8"+imm(D_INOP)+b"\xC6\x00\x00"                     # inop=0
        c+=b"\x48\xB8"+imm(D_CNT)+b"\xFF\x00"                          # cnt++
        L("skip")
        c+=bytes([0x9D,0x41,0x5B,0x41,0x5A,0x41,0x59,0x41,0x58,0x5B,0x5A,0x59,0x58])   # popfq; pops
        c+=stolen                                                     # prologo roubado REAL (dinamico, a prova de update)
        c+=b"\xE9"; rbp=len(c); c+=b"\x00\x00\x00\x00"
        c[rbp:rbp+4]=struct.pack("<i", back_va-(cave+rbp+4))
        for pos,l in fix: c[pos:pos+4]=struct.pack("<i", lab[l]-(pos+4))
        return bytes(c)
    def _orig_prologue(self, rva, n=24):
        """Bytes ORIGINAIS de um RVA lidos do GameAssembly.dll NO DISCO — fonte GARANTIDA p/ curar um
        hook orfao (o arquivo nunca tem os nossos patches), sem depender de cache."""
        return _dll_bytes(rva, n)
    def _plen_of(self, raw, base_addr, minlen=5, maxlen=8):
        """Instrucoes INTEIRAS cobrindo >=minlen a partir de BYTES (p/ o E9 jmp de 5). None se
        rip-rel/branch (nao da p/ relocar com seguranca)."""
        if not raw: return None
        try:
            import capstone
            md=capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); n=0
            for ins in md.disasm(raw, base_addr):
                if "rip" in ins.op_str or ins.mnemonic[0]=="j" or ins.mnemonic=="call": return None
                n+=ins.size
                if n>=minlen: return n if n<=maxlen else None
        except Exception: return None
        return None
    def _prologue_len(self, addr, minlen=5, maxlen=8):
        """Idem, lendo da memoria do jogo. Dinamico via capstone -> a prova de update."""
        return self._plen_of(self.rb(addr,24), addr, minlen, maxlen)
    def _install_dispatch(self):
        upd=self.sym.get("upd")
        if not upd or not self.sym.get("llx"): self.log("dispatcher: offset upd/llx missing"); return False
        UPD=self.base+upd
        cachef=os.path.join(CACHE,"upd_prologue_%s.bin"%(dll_hash() or "x"))
        head=self.rb(UPD,1)
        if not head: return False
        if head[0]==0xE9:                                     # hook ORFAO de sessao morta -> restaura o original
            orig=None
            try:                                              # 1) cache (rapido)
                c=open(cachef,"rb").read()
                if c and len(c)>=5: orig=c
            except Exception: pass
            if not orig:                                      # 2) FALLBACK GARANTIDO: le do GameAssembly.dll no
                raw=self._orig_prologue(upd,24)               #    DISCO (o arquivo nunca tem nossos hooks).
                p=self._plen_of(raw,UPD)                      #    Nao depende de cache nem da pasta do exe.
                if raw and p: orig=raw[:p]
            if not orig or len(orig)<5:
                self.log("dispatcher: nao consegui recuperar o prologo original do hook orfao"); return False
            hs=self._suspend_all()
            try: self.wb(UPD,orig)
            finally: self._resume_all(hs)
            self.log("dispatcher: hook orfao removido, prologo original restaurado")
        # prologo limpo: descobre DINAMICAMENTE quantos bytes roubar (nao hardcoda -> sobrevive a update)
        plen=self._prologue_len(UPD)
        if not plen:
            self.log("dispatcher: prologo do InputManager.Update nao-relocavel (rip/branch)"); return False
        stolen=self.rb(UPD,plen)
        try: open(cachef,"wb").write(stolen)                  # cacheia p/ self-heal numa sessao futura
        except Exception: pass
        cave=self.alloc_cave(UPD)
        if not cave: self.log("dispatcher: cave failed"); return False
        self.wb(cave+0x800,b"\x00"*0x50); self.wb(cave,self._dispatch_code(cave,UPD+plen,stolen))
        self.wb(cave+0x900,b"\xC3")                            # ret p/ o fake delegate
        self.wb(cave+0x920,b"\x00"*0x48); self.wb(cave+0x920+0x18,struct.pack("<Q",cave+0x900))  # fake[+0x18]=ret
        patch=b"\xE9"+struct.pack("<i",cave-(UPD+5))+b"\x90"*(plen-5)
        hs=self._suspend_all()
        try: self.wb(UPD,patch)
        finally: self._resume_all(hs)
        self.wb(cave+0x800,b"\x01")                            # en
        self.abx={"cave":cave,"data":cave+0x800,"upd":UPD,"stolen":bytes(stolen)}
        self.log("Dispatcher installed @ %#x (prologo %d bytes)"%(cave,plen)); return True
    def _remove_dispatch(self):
        if not self.abx: return
        try:
            self.wb(self.abx["data"],b"\x00")                   # en=0
            hs=self._suspend_all()
            try: self.wb(self.abx["upd"],self.abx["stolen"])
            finally: self._resume_all(hs)
            self.log("Dispatcher removed (Update restored)")
        except Exception: pass
        self.abx=None
    def _dispatch(self, cmd, argP=0, argI=0, req=None, arg2=0, timeout=0.8):
        """Executa 1 comando na main-thread do jogo (via cave). Serializado, bloqueante.
        req = tupla de 5 ints (MoveRequest) p/ cmd stash. arg2 = 2o int (ex: cmd9 ioa index)."""
        if not self.abx: return False
        with self._disp_lock:
            d=self.abx["data"]
            for _ in range(30):                                # espera comando anterior ser consumido
                if self.rb(d+1,1)==b"\x00": break
                time.sleep(0.015)
            if req is not None: self.wb(d+0x20,struct.pack("<iiiii",*req))   # MoveRequest
            self.wb(d+0x18,struct.pack("<i",arg2))                            # arg2 (cmd9 index)
            self.wb(d+8,struct.pack("<Q",argP)); self.wb(d+0x10,struct.pack("<i",argI))
            self.wb(d+4,struct.pack("<i",cmd)); self.wb(d+1,b"\x01")   # argP,argI,cmd,doFlag
            got=False
            for _ in range(max(1,int(timeout/0.015))):
                if self.rb(d+1,1)==b"\x00": got=True; break     # consumido
                time.sleep(0.015)
            # o move via iw lanca NRE (callback null) e o unwind pula o inop=0 do cave ->
            # reseta inop aqui pra nao travar o proximo comando
            self.wb(d+2,b"\x00")
            return got
    def _last_ret(self):
        """Valor de retorno (rax&0xffffffff) do ultimo comando do dispatcher. Ex: EAddCubeResult do ioa (cmd9)."""
        if not self.abx: return None
        r=self.rb(self.abx["data"]+0x40,8)
        return (int.from_bytes(r,"little") & 0xffffffff) if r else None
    def _dispatch_call(self, func_va, argP=0, argI=0, timeout=1.0):
        """Chama func_va(rcx=argP, edx=argI) na MAIN-THREAD (cmd12). Retorna rax&0xffffffff.
        P/ funcs sem afinidade-async que precisam da main-thread (ex inl/inm de clear do cubo)."""
        if not self.abx or not func_va: return None
        with self._disp_lock:
            self.wb(self.abx["data"]+0x38, struct.pack("<Q", func_va))   # D_FUNC
        ok=self._dispatch(12, argP=argP, argI=argI, timeout=timeout)
        return self._last_ret() if ok else None
    def _free_stash_slot(self, exclude=()):
        """Index de um slot LIVRE (desbloqueado, vazio) no stash, fora de 'exclude'. None se cheio."""
        bau=self._resolve_bau()
        if not bau: return None
        PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
        lp=self.u64(PSD+self.sym.get("stash_off",0x90)); arr=self.u64(lp+0x10); sz=self.u32(lp+0x18)
        if not arr or sz is None: return None
        for i in range(min(sz,400)):
            o=self.u64(arr+0x20+i*8)
            if o and (self.rb(o+0x20,1) or b"\x00")[0] and self.u64(o+0x18)==0:
                idx=self.u32(o+0x10)
                if idx not in exclude: return idx              # StashSaveData.Index
        return None
    # --- singleton 'ra' (gerente de move de itens) ---
    def _ra(self):
        c=self.cache.get("ra_inst")
        if c and self.u64(c)==self.cache.get("ra_klass"): return c
        klass=self._klass_by_name("", self.sym.get("ra_class") or "ra")   # nome ofuscado muda (ra->qz); usa o extraido
        if not klass: return None
        kp=struct.pack("<Q",klass)
        for base,size in self._mem_regions((0x04,0x40)):
            d=self.rb(base,size)
            if not d: continue
            j=d.find(kp)
            while j>=0:
                if j%8==0:
                    a=base+j
                    if self.u64(a+8)==0 and self.vptr(self.u64(a+0x10)):   # monitor==0 && m_CachedPtr valido
                        self.cache["ra_inst"]=a; return a
                j=d.find(kp,j+8)
        return None
    def _klass_by_name(self, ns, name):
        ck="klass_%s_%s"%(ns,name)
        if ck in self.cache: return self.cache[ck]
        E={n:self._export(n) for n in ("il2cpp_domain_get","il2cpp_domain_get_assemblies","il2cpp_assembly_get_image","il2cpp_class_from_name")}
        if not all(E.values()): return 0
        sc=self.cache.get("rc_scratch2")
        if not sc:
            ctypes.windll.kernel32.VirtualAllocEx.restype=ctypes.c_void_p
            sc=ctypes.windll.kernel32.VirtualAllocEx(self.pm.process_handle,None,0x1000,0x3000,0x40); self.cache["rc_scratch2"]=sc
        if not sc: return 0
        NS=sc+0x40; NM=sc+0x80; SZ=sc+0xC0
        self.wb(NS,ns.encode()+b"\x00"); self.wb(NM,name.encode()+b"\x00")
        dom=self._remote_call(E["il2cpp_domain_get"],[])
        asms=self._remote_call(E["il2cpp_domain_get_assemblies"],[dom,SZ]); n=self.u64(SZ) or 0
        for i in range(min(n,200)):
            a=self.u64(asms+i*8)
            if not a: continue
            kl=self._remote_call(E["il2cpp_class_from_name"],[self._remote_call(E["il2cpp_assembly_get_image"],[a]),NS,NM])
            if kl: self.cache[ck]=kl; self.cache["ra_klass" if name=="ra" else ck]=kl; return kl
        return 0
    # --- leitura dos slots do INVENTARIO (nao stash): livres + ocupados por (idx,uid,itemKey) ---
    def read_inv_slots(self):
        with self.lock:
            bau=self._resolve_bau()
            if not bau: return None
            PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            if not self.vptr(PSD): return None
            def rl(lp,fields):
                arr=self.u64(lp+0x10); size=self.u32(lp+0x18); out=[]
                if not arr or size is None or not (0<=size<300): return out
                for i in range(size):
                    obj=self.u64(arr+0x20+i*8); d=None
                    if obj:
                        d={}
                        for nm,off,sz in fields: d[nm]=int.from_bytes(self.rb(obj+off,sz) or b"\x00"*sz,"little")
                    out.append(d)
                return out
            master=rl(self.u64(PSD+self.sym.get("inv_list_off",INV_LIST_OFF)),[("key",0x10,4),("uid",0x18,8)])
            u2k={m["uid"]:m["key"] for m in master if m}
            slots=rl(self.u64(PSD+self.sym.get("inv_slots_off",0x88)),[("idx",0x10,4),("uid",0x18,8),("unlock",0x20,1)])
            free=0; occ=[]
            for s in slots:
                if not s or not s["unlock"]: continue
                if s["uid"]==0: free+=1
                else: occ.append((s["idx"],s["uid"],u2k.get(s["uid"],0)))
            return {"free":free,"occ":occ}
    # === AUTOMACAO: auto-caixa + auto-item (stash) — compartilham o dispatcher ===
    def apply_automation(self):
        item=self.want.get("autoitem") or self.want.get("autosynth")
        need=self.want.get("autobox") or item or self.want.get("autoboss") or self.want.get("evolve")
        if need:
            # SELF-HEAL: se o abx esta setado mas o hook sumiu do jogo (prologo != E9 - ex: outro
            # processo removeu, ou o jogo reiniciou), descarta o abx fantasma pra RE-INSTALAR sozinho.
            if self.abx:
                patch=self.rb(self.abx["upd"],5)           # E9 <rel32> — checa se o hook ainda e NOSSO
                mine=False
                if patch and patch[0]==0xE9:
                    rel=struct.unpack("<i",patch[1:5])[0]
                    mine=((self.abx["upd"]+5+rel)==self.abx["cave"])   # aponta pro nosso cave? senao e stray/morto
                if not mine: self.abx=None                  # hook sumiu OU e de outra sessao -> re-instala
            if not self.abx: self._install_dispatch()
            if self.abx and not (getattr(self,"_auto_thr",None) and self._auto_thr.is_alive()):
                # UM SO loop (caixa->stash->fuse) numa thread. Antes eram 2 threads disputando o mesmo
                # dispatcher -> o auto-box ficava faminto (starvation) quando o auto-fuse rodava junto.
                self._auto_thr=threading.Thread(target=self._auto_loop,daemon=True); self._auto_thr.start()
        elif self.abx:
            self._remove_dispatch()
    def _auto_loop(self):
        """LOOP UNICO de automacao: caixa (PRIORIDADE) -> stash -> synthesis, em UMA thread e UM
        dispatcher. Substitui as 2 threads antigas que disputavam o dispatcher e faziam o auto-box
        parar (starvation) quando o auto-fuse rodava junto. A caixa e checada TODA iteracao -> abre
        na hora que dropa."""
        used=set()
        W=self.want
        while (W.get("autobox") or W.get("autoitem") or W.get("autosynth") or W.get("autoboss") or W.get("evolve")) and self.abx and self.pm:
            try:
                did=False
                if W.get("autobox") and self._do_autobox(): did=True          # 1) caixa: prioridade maxima
                if (W.get("autoitem") or W.get("autosynth")) and self._do_stash_bulk(used): did=True  # 2) stash EM LOTE (rapido)
                if W.get("autosynth") and self._do_synth(): did=True          # 3) fuse — NAO gatear por 'not did' (senao o box/stash o matam de fome); _do_synth e rapido quando nao ha 9
                if W.get("autoboss") and not did and self._do_autoboss(): did=True  # 4) act boss: gasta 1 soulstone e volta
                if W.get("evolve") and not did and self._do_evolve(): did=True      # 5) evolucao: sempre na fase mais nova
                if W.get("autoitem") and not did and self._sort_grade_step(2): did=True  # 5) ordena o stash por grade (so quando ocioso)
                time.sleep(0.12 if did else 0.5)
            except Exception: time.sleep(0.3)
    # ===================== MAPA / NAVEGACAO DE ESTAGIO =====================
    # StageKey = (dificuldade+1)*1000 + act*100 + estagio  (validado 120/120 na tabela viva).
    # NORMAL 1101-1310 · NIGHTMARE 2101-2310 · HELL 3101-3310 · TORMENT 4101-4310.
    DIFFS=("NORMAL","NIGHTMARE","HELL","TORMENT")
    @staticmethod
    def stage_name(k):
        try: return "%s %d-%d"%(Engine.DIFFS[k//1000-1],(k%1000)//100,k%100)
        except Exception: return str(k)
    def _uo_sf(self):
        """static_fields da classe estatica de estagio (uu.uo)."""
        ti=self.sym.get("uo_ti")
        if not ti: return None
        klass=self.u64(self.base+ti)
        return self.u64(klass+0xB8) if self.vptr(klass) else None
    def _obs_int(self, addr):
        """ObscuredInt (ACTk, 16B): hash@0, hidden@4, key@8, fake@0xC -> ((hidden-key)&0xFFFFFFFF)^key"""
        raw=self.rb(addr,16)
        if not raw or len(raw)<16: return None
        h,hid,k,f=struct.unpack("<iiii",raw)
        p=((hid-k)&0xFFFFFFFF)^(k&0xFFFFFFFF)
        return p-0x100000000 if p>=0x80000000 else p
    def _obs_set(self, addr, value):
        """Escreve `value` num ObscuredInt (ACTk) mexendo SO no hiddenValue@+4 com a key@+8 atual.
        NAO toca hash@0 / key@8 / fake@0xC -> escrita MINIMA (o encode bate exato com o do jogo:
        hidden = ((v ^ key) + key) & 0xFFFFFFFF). Deixar fake/key intactos = menor chance de acordar
        o honeypot do detector. Retorna True se o decode confirma o valor."""
        raw=self.rb(addr,16)
        if not raw or len(raw)<16: return False
        _,_,k,_=struct.unpack("<iiii",raw); k&=0xFFFFFFFF
        newhid=((int(value)^k)+k)&0xFFFFFFFF
        self.wb(addr+4, struct.pack("<I", newhid))
        return self._obs_int(addr)==int(value)
    def stage_progress(self):
        """(maxCompletedStage, currentStageKey, wave). max = maior key liberado (regra jgq: key<=max)."""
        sf=self._uo_sf()
        if not sf: return (None,None,None)
        g=lambda o: self._obs_int(sf+o) if o else None
        return (g(self.sym.get("uo_max")), g(self.sym.get("uo_cur")), g(self.sym.get("uo_wave")))
    STAGE_MAX_KEY=4310                                 # TORMENT 3-10 = maior key (libera 120/120)
    def set_maxstage(self, value=None):
        """Desbloqueia estagios ate `value` (default 4310 = TODOS os 120). Escreve o ObscuredInt
        RUNTIME uo_max (autoritativo; o save espelha ele) + o int do save (CommonSaveData.maxCompletedStage
        @ PSD+0x10 -> +off) por consistencia. Retorna (ok_runtime, valor).
        AVISO (provado ao vivo): escrever o ObscuredInt runtime dispara o honesty-check periodico do ACTk
        (~12s) -> o jogo FECHA sozinho (Application.Quit). NOPar o ynj NAO impede (o caminho de Quit e outro).
        MAS o valor PERSISTE (auto-save na janela de ~12s) e recarrega LIMPO -> reabra e esta liberado."""
        value=self.STAGE_MAX_KEY if value is None else int(value)
        sf=self._uo_sf(); off=self.sym.get("uo_max")
        if not (sf and off): return False, value
        ok=bool(self._obs_set(sf+off, value))
        try:                                           # espelha no save int (best-effort)
            psd,_=self._player_psd()
            if psd:
                csd=self.u64(psd+0x10)
                if self.vptr(csd): self.wb(csd+self.sym.get("commonsave_maxstage",0x54), struct.pack("<i", value))
        except Exception: pass
        return ok, value
    def _stage_cache(self, key):
        """StageCache do stageKey, pelo Dictionary<int,StageCache> do proprio jogo."""
        sf=self._uo_sf(); off=self.sym.get("uo_dict")
        if not sf or not off: return None
        d=self.u64(sf+off)
        if not self.vptr(d): return None
        ent=self.u64(d+0x18); n=self.u32(d+0x20)
        if not self.vptr(ent) or not n or n>4096: return None
        for i in range(n):
            a=ent+0x20+i*0x18
            if self.u32(a+8)==key: return self.u64(a+0x10)
        return None
    def stage_unlocked(self, key):
        """jgq(key) do proprio jogo (= key <= maxCompletedStage). So le."""
        f=self.sym.get("jgq")
        if not f: return None
        return bool(self._dispatch_call(self.base+f, argP=key))
    def stage_can_enter(self, key):
        """jgc(cache) do proprio jogo: 0=Success 1=EndStage 2=NeedSoulStone 3=NeedChestSpace. So le.
        Melhor que checar inventario na mao: ele tambem confere ESPACO DE BAU."""
        f=self.sym.get("jgc"); c=self._stage_cache(key)
        if not f or not c: return None
        return self._dispatch_call(self.base+f, argP=c)
    ENTER_RESULT={0:"Success",1:"FailReasonEndStage",2:"FailReasonNeedSoulStone",3:"FailReasonNeedChestSpace",4:"Failed"}
    def goto_stage(self, key):
        """Vai pra um estagio NORMAL. jgk e o passo final de entrada dos dois caminhos do jogo."""
        f=self.sym.get("jgk")
        if not f: return False
        if not self._stage_cache(key): return False          # key inexistente -> jgk lanca KeyNotFound
        self._dispatch_call(self.base+f, argP=key)
        return True
    def enter_boss(self, key):
        """Entra num ACTBOSS (x-10) reproduzindo o CLIQUE: jgd(cache,FAKE) + jgk(key).
        Por que o par: o Action<bool> que o clique passa NAO e cosmetico — ele chama o jgk. jgd sozinho
        reservaria a soulstone (beyt) e NAO carregaria a fase (cliente meio-feito)."""
        if not (self.sym.get("jgd") and self.sym.get("jgk")): return False
        c=self._stage_cache(key)
        if not c: return False
        self._dispatch(13, argP=c, timeout=1.5)      # jgd: marca o retorno (beyq) + reserva a pedra
        time.sleep(0.15)
        self._dispatch_call(self.base+self.sym["jgk"], argP=key)   # o que o callback faria
        return True
    def stage_table(self):
        """{stageKey:{next,type,ss,lvl}} lido da tabela VIVA do jogo (120 estagios). Cacheado."""
        c=self.cache.get("stage_tbl")
        if c: return c
        ti=self.sym.get("bal_ti"); off=self.sym.get("stage_off")
        if not (ti and off): return {}
        klass=self.u64(self.base+ti)
        if not self.vptr(klass): return {}
        bal=self.u64(self.u64(klass+0xB8))
        lst=self.u64(bal+off) if self.vptr(bal) else 0
        if not self.vptr(lst): return {}
        arr=self.u64(lst+0x10); n=self.u32(lst+0x18)
        if not self.vptr(arr) or not n or n>500: return {}
        t={}
        for i in range(n):
            o=self.u64(arr+0x20+i*8)
            if not self.vptr(o): continue
            k=self.u32(o+0x30)
            if k: t[k]={"next":self.u32(o+0xA0),"type":self.u32(o+0x40),"ss":self.u32(o+0x94),"lvl":self.u32(o+0x50)}
        if len(t)>=100: self.cache["stage_tbl"]=t
        return t
    def _boss_run(self, boss):
        """Entra num x-10, espera o desfecho e deixa o jogo voltar. True se o boss morreu."""
        volta=self.stage_progress()[1]
        if not self.enter_boss(boss): self.log("boss: falhou ao entrar em %s"%self.stage_name(boss)); return False
        ok=self._wait_boss_done(boss, timeout=600)
        self.log("boss %s: %s"%(self.stage_name(boss), "✅ morto (caixa dropou)" if ok else
                                ("entrada nao pegou" if ok is None else "saiu sem matar (party morreu)")))
        if ok:                                        # a volta e do jogo (beyq); so intervenho se travar
            t0=time.time()
            while self.stage_progress()[1]==boss and time.time()-t0<15: time.sleep(0.5)
            if self.stage_progress()[1]==boss and volta: self.goto_stage(volta)
        return bool(ok)
    def _do_evolve(self):
        """MODO EVOLUCAO: mantem voce sempre na fase mais nova liberada, ate o Torment 3-9.
        Como funciona: nao existe lista de fases liberadas — o progresso e UM int
        (maxCompletedStage) que E a maior key liberada. Entao 'evoluir' = estar nele.
        Zerou a fase? o jogo sobe o max sozinho -> o bot te leva pra proxima. Chegou num x-10?
        entra com a soulstone (matar o boss e o que libera o act/dificuldade seguinte).
        Nao pula nada: e a mesma corrente NextStageKey que o jogo usa."""
        ALVO=4309                                     # Torment 3-9 (o 3-10 fica pro switch Auto-boss)
        mx,cur,_=self.stage_progress()
        if not mx or not cur: return False
        alvo=min(mx, ALVO)
        if cur>=alvo: return False                    # ja esta na fase mais nova -> farmando pra zerar
        t=self.stage_table()
        info=t.get(alvo)
        if not info: return False
        if info["type"]==1:                           # o alvo e um x-10: so entra com a pedra
            r=self.stage_can_enter(alvo)
            if r!=0:
                if r==2: self.log("📈 evolucao: %s precisa da soulstone %s — farmando ate cair"%(
                                   self.stage_name(alvo), info["ss"]))
                else: self.log("📈 evolucao: %s -> %s"%(self.stage_name(alvo), self.ENTER_RESULT.get(r,r)))
                return False
            self.log("📈 evolucao: %s liberado — indo pro boss com a soulstone"%self.stage_name(alvo))
            return self._boss_run(alvo)
        self.log("📈 evolucao: %s -> %s (nivel %s)"%(self.stage_name(cur), self.stage_name(alvo), info["lvl"]))
        return self.goto_stage(alvo)
    def _do_autoboss(self):
        """MINIMO, so o basico: se tem soulstone, entra no x-10 daquela dificuldade e DEIXA O JOGO
        VOLTAR sozinho. NAO liga/desliga nenhum cheat — se voce roda com Hitkill, o boss morre; se nao,
        a party morre e o jogo te devolve igual, sem prejuizo (a pedra so e cobrada quando o boss MORRE,
        entao entrar e reversivel).
        A volta e nativa: o jgd grava a fase de onde voce veio como ponto de retorno (beyq) — por isso
        entrar no 3-10 do Hell vindo do Torment 3-9 devolve pro Torment 3-9."""
        PARES=((190004,4310),(190003,3310))               # Torment primeiro, depois Hell
        if not self.sym.get("jgd"): self.log("auto-boss: offsets de estagio ausentes"); return False
        inv=self.read_inventory() or {}
        for ss,boss in PARES:
            if inv.get(ss,0)<=0: continue
            r=self.stage_can_enter(boss)                  # gate do proprio jogo (pedra + espaco de bau)
            if r!=0:
                self.log("auto-boss: %s -> %s"%(self.stage_name(boss), self.ENTER_RESULT.get(r,r)))
                continue
            volta=self.stage_progress()[1]                # de onde vim = pra onde o jogo vai me devolver
            self.log("🗡 auto-boss: entrando em %s (soulstone %s: %d)"%(self.stage_name(boss),ss,inv[ss]))
            if not self.enter_boss(boss): self.log("auto-boss: falhou ao entrar"); return False
            # timeout GENEROSO: sem hitkill o act boss leva ~3min (medido: 156s numa rodada, >180s
            # noutra). Timeout curto fazia o bot ARRANCAR o jogador de uma luta que ia ganhar.
            ok=self._wait_boss_done(boss, timeout=600)
            self.log("auto-boss: %s"%("✅ boss morto (caixa dropou)" if ok else
                                      ("entrada nao pegou" if ok is None else "saiu sem matar (party morreu)")))
            # A volta e do JOGO (beyq) — nao forco. So intervenho se ele travar DEPOIS de matar.
            if ok:
                t0=time.time()
                while self.stage_progress()[1]==boss and time.time()-t0<15: time.sleep(0.5)
                if self.stage_progress()[1]==boss and volta:
                    self.log("auto-boss: o jogo travou no boss — devolvendo pro %s"%self.stage_name(volta))
                    self.goto_stage(volta)
            return bool(ok)
        return False
    def _wait_boss_done(self, boss, timeout=180):
        """Espera o desfecho da luta. Devolve True (boss morto), False (saiu sem matar = party morreu ou
        desistiu) ou None (a entrada nem pegou).
        DUAS FASES, e a 1a e obrigatoria: antes de julgar 'acabou' e preciso CONFIRMAR que a entrada
        pegou — senao 'fase != boss' e verdade no instante 0 e da falso-positivo imediato (foi o bug do
        1o teste: reportou 'boss morto' em 5s sem pedra cobrada e sem caixa).
        Boss morto = a CAIXA de ACTBOSS aparecer. Sair da fase sem caixa = derrota."""
        sf=self._uo_sf(); co=self.sym.get("uo_cur")
        if not (sf and co): return None
        cur=lambda: self._obs_int(sf+co)
        t0=time.time()
        while cur()!=boss:                                    # 1) a entrada pegou?
            if time.time()-t0>8:
                self.log("auto-boss: a entrada nao pegou (fase=%s)"%cur()); return None
            time.sleep(0.3)
        b0=self._iuw_count(2) or 0
        t0=time.time()
        while time.time()-t0<timeout:                          # 2) desfecho
            time.sleep(1.0)
            if not self.want.get("autoboss"): return False
            b=self._iuw_count(2)
            if b is not None and b>b0: return True             # caixa do boss caiu = MATOU
            if cur()!=boss:                                    # saiu sem caixa = party morreu
                time.sleep(1.0)
                b=self._iuw_count(2)
                return bool(b is not None and b>b0)
        return False
    def _do_autobox(self):
        """Abre TODAS as caixas esperando (iuw>0). Retorna True se abriu alguma. llx SEMPRE abre se
        iuw>0 && iyf()==0 (provado, ate com o Cubo aberto). A recompensa pode ser ouro/gema -> o
        inventario nao muda, mas iuw decrementa = caixa aberta -> loga."""
        boxes=self._find_stageboxes()
        if not boxes: return False
        klass=self.cache.get("sb_klass")
        if not (self.sym.get("iuw") and klass):
            order=[boxes[t] for t in (0,1,2) if t in boxes]                   # fallback cego (sem iuw)
            for tgt in order:
                if self._valid_stagebox(tgt, klass): self._dispatch(1, argP=tgt)
            return False
        NAMES={0:"NORMAL",1:"BOSS",2:"ACTBOSS"}; opened=False
        for t in (0,1,2):
            tgt=boxes.get(t)
            if not tgt: continue
            cnt=self._iuw_count(t); guard=0
            while cnt and cnt>0 and guard<15 and self.want.get("autobox") and self.abx:
                if not self._valid_stagebox(tgt, klass):                     # morreu no meio -> re-scan
                    self.cache.pop("sb_inst", None)                          # (nunca clicar em cadaver: crasha)
                    tgt=self._find_stageboxes().get(t)
                    if not tgt or not self._valid_stagebox(tgt, klass): break
                self._dispatch(1, argP=tgt, timeout=1.2)                     # llx main-thread
                time.sleep(0.15)
                new=self._iuw_count(t)
                if new is None: break
                if new<cnt:
                    self.log("🎁 Box %s opened%s"%(NAMES[t], (" (%d left)"%new) if new else ""))
                    opened=True; cnt=new; guard+=1
                else: break                                                  # nao decrementou -> para esse tipo
        return opened
    def _do_stash(self, used):
        """Move UM item do inventario pro stash (INVENTORY->STASH via iw). Retorna True se moveu.
        Blindado: re-verifica origem, exclui slots recem-usados, espera refletir."""
        ra=self._ra(); inv=self.read_inv_slots()
        if not (ra and inv): used.clear(); return False
        src=next(((i,u) for i,u,k in inv["occ"] if k), None)                 # 1o item ocupado com key
        if src is None: used.clear(); return False
        slot=self._free_stash_slot(exclude=used)
        if slot is None: return False                                        # stash cheio ou sem slot livre
        src_idx,src_uid=src
        if not self._inv_slot_has(src_idx, src_uid): return False
        self._dispatch(2, argP=ra, req=(1, src_idx, 2, slot, 1))            # INVENTORY->STASH
        used.add(slot)
        if len(used)>30: used.clear()
        for _ in range(40):                                                 # espera refletir
            inv2=self.read_inv_slots()
            if not inv2 or not any(u==src_uid for _,u,_ in inv2["occ"]): break
            time.sleep(0.03)
        return True
    def _slot_objs(self, off):
        """Ponteiros dos slots de uma List (inv/stash) em 1 leitura BULK (rapido)."""
        bau=self._resolve_bau()
        if not bau: return None, []
        PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
        if not self.vptr(PSD): return None, []
        lp=self.u64(PSD+off); arr=self.u64(lp+0x10); sz=self.u32(lp+0x18) or 0
        sz=min(sz,4000)
        if not arr or sz==0: return PSD, []
        raw=self.rb(arr+0x20, sz*8) or b""
        return PSD,[int.from_bytes(raw[i*8:i*8+8],"little") for i in range(sz) if len(raw)>=i*8+8]
    def _do_stash_bulk(self, used, maxn=150):
        """Move VARIOS itens inv->stash de uma vez: le inv+stash 1x (bulk, 1 read/slot), calcula todos
        os pares (src->slot livre) e dispara em RAJADA — SEM reflect-wait entre cada. ~50x mais rapido
        que 1-por-vez. Retorna quantos moveu."""
        ra=self._ra()
        if not ra: return 0
        _,inv_slots=self._slot_objs(self.sym.get("inv_slots_off",0x88))
        srcs=[]
        for o in inv_slots:                                    # itens ocupados no inventario
            if not o: continue
            d=self.rb(o+0x10,0x11)                             # idx@0x10, uid@0x18, unlock@0x20 numa leitura
            if not d or len(d)<0x11 or not d[0x10]: continue   # unlock==0 -> pula
            if int.from_bytes(d[8:16],"little")!=0:            # uid!=0 -> ocupado
                srcs.append(int.from_bytes(d[0:4],"little"))   # idx
        if not srcs: return 0
        _,stash_slots=self._slot_objs(self.sym.get("stash_off",0x90))
        slots=[]
        for o in stash_slots:                                  # slots LIVRES no stash
            if not o: continue
            d=self.rb(o+0x10,0x11)
            if not d or len(d)<0x11 or not d[0x10]: continue
            if int.from_bytes(d[8:16],"little")==0:            # uid==0 -> livre
                idx=int.from_bytes(d[0:4],"little")
                if idx not in used: slots.append(idx)
                if len(slots)>=len(srcs): break
        n=min(len(srcs),len(slots),maxn)
        for k in range(n):                                     # RAJADA: 1 dispatch por item, sem reflect-wait
            self._dispatch(2, argP=ra, req=(1, srcs[k], 2, slots[k], 1))
            used.add(slots[k])
        if len(used)>800: used.clear()
        return n
    # ===================== ALCHEMY (vender equipamento de nivel baixo por ouro) =====================
    # Alchemy = recipe do cubo que converte item -> OURO (server-side, so uniqueId de item REAL -> seguro,
    # mesma classe de abrir caixa/soulstone). Fluxo = igual a synthesis: seleciona a recipe ALCHEMY
    # (inf(beru[0])), adiciona os itens (ioa), dispara (imx). PROCESSA EM LOTE (vende tudo que estiver no cubo).
    def _cube_recipe(self, rtype):
        """objeto uw da recipe `rtype` (ERecipeType) via beru @cube_sf+0xF0 = Dictionary<ERecipeType,uw>."""
        sf=self._cube_sf()
        if not sf: return None
        d=self.u64(sf+self.OFF["beru"])
        if not self.vptr(d): return None
        ent=self.u64(d+0x18); n=self.u32(d+0x20)
        if not self.vptr(ent) or not n or n>64: return None
        for i in range(n):
            a=ent+0x20+i*0x18
            if self.u32(a+8)==rtype: return self.u64(a+0x10)
        return None
    def _inv_index_items(self):
        """[(index, itemKey, uniqueId)] da lista PlayerSaveData.itemSaveDatas (inventario, ESlotType.INVENTORY).
        O index E o que o ioa(INVENTORY, index) espera."""
        bau=self._resolve_bau()
        if not bau: return []
        po=self.sym.get("inv_psd_off",INV_PSD_OFF); lo=self.sym.get("inv_list_off",INV_LIST_OFF)
        psd=self.u64(bau+po); lst=self.u64(psd+lo) if psd else 0
        if not self.vptr(lst): return []
        arr=self.u64(lst+0x10); size=self.u32(lst+0x18)
        if not self.vptr(arr) or size is None or not (0<=size<500000): return []
        out=[]
        for i in range(size):
            it=self.u64(arr+0x20+i*8)
            if not it: continue
            out.append((i, self.u32(it+ITEMSAVE_KEY_OFF), self.u64(it+0x18)))   # index, itemKey, uniqueId
        return out
    def alchemy_candidates(self, level_cap=55):
        """[(index,key,uniqueId,level,synth)] dos itens VENDAVEIS: so EQUIPAMENTO/ACESSORIO de nivel 1..cap.
        FILTRO OBRIGATORIO alem do nivel: EItemType==GEAR(2) E EItemSynthesisType in {Gear=0,Accessory=1}.
        Sem isso 'Level<=55' varreria material/soulstone/caixa (todos leem Level=0)."""
        out=[]
        for idx,key,uid in self._inv_index_items():
            if not key: continue
            info=self._item_info(key)                       # (EItemType, grade, synthType, Level)
            if not info: continue
            itype,grade,synth,lvl=info
            if itype==2 and synth in (0,1) and lvl and 1<=lvl<=level_cap:
                out.append((idx,key,uid,lvl,synth))
        return out
    def read_gold(self):
        """Ouro atual (currency key 100001). Read-only. None se nao achar."""
        try:
            sf=self._cube_sf()
            # o ouro fica na CurrencyManager; reuso o mesmo padrao nq<T>. Se nao tiver helper, retorna None
            return self._currency(100001)
        except Exception:
            return None
    # ============ CUBE SYNTHESIS: dropdowns de tipo / nivel / grade ============
    # Offsets no cube_sf (= static_fields da classe Cube), DESCOBERTOS POR DIFF AO VIVO (o usuario mudou
    # cada dropdown na tela e eu comparei a memoria antes/depois):
    #   berp @0xC8  = EGradeType selecionado         (0=common 1=uncommon 2=rare 3=legendary+)
    #   bers @0xE0  = Dictionary<ERecipeType,List<uw>>. bers[SYNTHESIS=1] = os 8 TIERS de nivel do dropdown,
    #                 em ordem ASCENDENTE (Lv.1~10 ... Lv.65~80). **A ULTIMA (indice 7) = Lv.65~80** (confirmado pelo user).
    #   betd @0x254 = EItemSynthesisType selecionado (0=Gear/Equipment 1=Accessory 2=Material) <- o dropdown da setinha
    #   bete @0x258 = a recipe (uw) do TIER de nivel ATUAL. **inf(uw) @cmd6 SETA isto** = e assim que se muda o nivel.
    # Funcoes (dispatcher): ilo/cmd3=iln(EItemSynthesisType) seleciona o TIPO · ili/cmd7=ilh(EGradeType) o GRADE ·
    # inf/cmd6=ine(uw) seleciona o TIER de nivel. ATENCAO: trocar o TIPO (ilo) RESETA o nivel -> re-chamar _synth_set_lv6580.
    SYNTH_TYPE_OFF=0x254; SYNTH_GRADE_OFF=0xC8; SYNTH_LVRECIPE_OFF=0x258   # defaults c824; _load_offsets sobrescreve do sym
    OFF={"inlist":0x100,"active":0x140,"busy":0x150,"grade":0xC8,"beru":0xF0,"bers":0xE0,"recipe":0x240,
         "cubetype":0x254,"lvrecipe":0x258,"it_type":0x34,"it_grade":0x38,"it_synth":0x48,"it_level":0x6C}
    def _synth_recipes(self):
        """As recipes de SYNTHESIS (os tiers de nivel do dropdown). Ultima = Lv.65~80."""
        sf=self._cube_sf()
        if not sf: return []
        d=self.u64(sf+self.OFF["bers"])
        if not self.vptr(d): return []
        ent=self.u64(d+0x18); n=self.u32(d+0x20); syn=0
        for i in range(n or 0):
            a=ent+0x20+i*0x18
            if self.u32(a+8)==1: syn=self.u64(a+0x10); break     # ERecipeType.SYNTHESIS=1
        if not self.vptr(syn): return []
        arr=self.u64(syn+0x10); m=self.u32(syn+0x18)
        if not self.vptr(arr) or not m or m>64: return []
        return [self.u64(arr+0x20+i*8) for i in range(m)]
    def _synth_type(self):
        sf=self._cube_sf(); return self.u32(sf+self.SYNTH_TYPE_OFF) if sf else None
    def _synth_grade(self):
        sf=self._cube_sf(); return self.u32(sf+self.SYNTH_GRADE_OFF) if sf else None
    def _synth_set_lv6580(self):
        """Forca o dropdown de nivel pra Lv.65~80 (a ULTIMA recipe de synthesis) via inf. True se setou.
        Precisa do cubo ABERTO. Chamar SEMPRE apos trocar o tipo (ilo reseta o nivel)."""
        recs=self._synth_recipes()
        if not recs: return False
        target=recs[-1]                                          # Lv.65~80 = ultimo tier
        self._dispatch(6, argP=target, timeout=1.2); time.sleep(0.15)
        sf=self._cube_sf()
        return bool(sf and self.u64(sf+self.SYNTH_LVRECIPE_OFF)==target)
    def _synth_set_type(self, t):
        """ilo(EItemSynthesisType): 0=Equipment/Gear 1=Accessory 2=Material. RESETA o nivel -> chamar _synth_set_lv6580 depois."""
        self._dispatch(3, argI=t, timeout=1.0); time.sleep(0.15)
        sf=self._cube_sf(); return bool(sf and self.u32(sf+self.SYNTH_TYPE_OFF)==t)
    def _synth_set_grade(self, g):
        """ili(EGradeType) seleciona o grade."""
        self._dispatch(7, argI=g, timeout=1.0); time.sleep(0.1)
        sf=self._cube_sf(); return bool(sf and self.u32(sf+self.SYNTH_GRADE_OFF)==g)
    def _uimanager(self):
        """Instancia singleton nq<UIManager> (via TypeInfo). uimain = [UM+0xA8]."""
        ti=self.sym.get("uimgr_ti")
        if not ti: return None
        klass=self.u64(self.base+ti); sf=self.u64(klass+0xB8) if self.vptr(klass) else 0
        return self.u64(sf) if self.vptr(sf) else None      # nq<T> instancia = [static_fields+0]
    def _cube_is_open(self):
        """cubo aberto = recipe ativa (bese@sf+0x140 != 0)."""
        sf=self._cube_sf()
        return bool(sf and self.vptr(self.u64(sf+self.OFF["active"])))
    def _open_cube(self, timeout=4.0):
        """Abre o cubo reproduzindo o clique em button_Cube: eby(uiMain) na main-thread. Client-side,
        sem call server-side (confirmado). eby tem as guardas do clique real: so abre no MAIN screen sem
        popup -> se o jogo estiver noutra tela, nao abre (mostra toast)."""
        if self._cube_is_open(): return True
        um=self._uimanager()
        eby=self.sym.get("eby")
        if not (um and eby): self.log("alchemy: UIManager/eby ausente"); return False
        uimain=self.u64(um+0xA8)
        if not self.vptr(uimain): return False
        # eby so abre no MAIN screen sem popup. Tenta 3x com espera (a UI pode estar em transicao).
        for attempt in range(3):
            self._dispatch_call(self.base+eby, argP=uimain)
            t0=time.time()
            while time.time()-t0<timeout/3:
                if self._cube_is_open(): return True
                time.sleep(0.1)
            time.sleep(0.4)
        return False
    def _close_cube(self):
        """Fecha o painel aberto (hgr, sem argumento, this=UIManager)."""
        um=self._uimanager(); hgr=self.sym.get("hgr")
        if um and hgr: self._dispatch_call(self.base+hgr, argP=um)
    def _select_recipe(self, rtype):
        """ilx(ERecipeType): SelectRecipe do jogo (beru.TryGetValue + ine). So pega com o cubo ABERTO e
        LIMPO (guarda besg@sf+0x150 dirty). ALCHEMY=0."""
        ilx=self.sym.get("ilx")
        if not ilx: return False
        self._dispatch_call(self.base+ilx, argI=rtype); time.sleep(0.2)
        sf=self._cube_sf()
        return bool(sf and self.u64(sf+self.OFF["active"])==self._cube_recipe(rtype))
    def _do_alchemy(self, level_cap=55, dry=False, max_batch=30, one=False):
        """Vende (alchemy) equipamento/acessorio de nivel <= level_cap. O BOT ABRE O CUBO sozinho.
        Sequencia validada: abrir(eby) -> selecionar ALCHEMY(ilx 0) -> ioa(itens) -> imx(vende) -> fechar.
        SEGURANCA: so mexe em alchemy_candidates (GEAR + synth 0/1 + 1..cap). Antes de DISPARAR confere que
        o cubo so tem uniqueIds da lista (senao ABORTA sem vender). one=True vende so 1 (teste)."""
        cands=self.alchemy_candidates(level_cap)
        if dry:
            self.log("🧪 alchemy DRY: %d equip/acess <= Lv.%d venderiam"%(len(cands), level_cap))
            return cands
        if not cands: return False
        if not all(self.sym.get(k) for k in ("ioa","imx","ilx","eby","uimgr_ti")):
            self.log("alchemy: offsets do cubo/UI ausentes"); return False
        if not self._open_cube(): self.log("alchemy: nao consegui abrir o cubo (tela errada/popup aberto?)"); return False
        sf=self._cube_sf()
        if not self._select_recipe(0):                        # cubo tem que estar LIMPO p/ trocar recipe
            self.log("alchemy: nao consegui selecionar ALCHEMY (cubo ocupado?)"); self._close_cube(); return False
        sold=0
        while cands and (self.want.get("alchemy") or one):
            okuids={uid for _,_,uid,_,_ in cands}
            batch=cands[:(1 if one else max_batch)]
            for idx,key,uid,lvl,synth in batch:
                self._dispatch(9, argI=1, arg2=idx, timeout=1.0)   # ioa(INVENTORY=1, index)
                time.sleep(0.04)
            time.sleep(0.15)
            incube=self._cube_uids(sf)
            if incube and not incube.issubset(okuids):
                self.log("⚠ alchemy: cubo tem item fora da lista — ABORTANDO (nada vendido)")
                self._close_cube(); return sold>0
            if not incube:
                self.log("alchemy: nada entrou no cubo — parando"); break
            g0=self.read_gold()
            self._dispatch(5, timeout=2.0)                    # imx: vende o lote
            for _ in range(200):
                if not self._synth_busy(): break
                time.sleep(0.05)
            time.sleep(0.4)
            g1=self.read_gold()
            n=len(incube); sold+=n
            gained=("(+%s ouro)"%(g1-g0)) if (g0 is not None and g1 is not None and g1>g0) else ""
            self.log("🧪 alchemy: vendeu %d equip/acess %s"%(n, gained))
            if one: break
            cands=self.alchemy_candidates(level_cap)          # re-enumera (indices mudaram)
        self._close_cube()
        return sold>0
    def _cube_uids(self, sf):
        """uniqueIds dos itens ATUALMENTE no cubo (berw = List<CubeInData> @sf+0x100)."""
        try:
            lst=self.u64(sf+self.OFF["inlist"])
            if not self.vptr(lst): return set()
            arr=self.u64(lst+0x10); n=self.u32(lst+0x18)
            if not self.vptr(arr) or not n or n>500: return set()
            out=set()
            for i in range(n):
                cd=self.u64(arr+0x20+i*8)
                if cd: out.add(self.u64(cd+0x10))            # CubeInData.uniqueId (confirmar offset no teste)
            return out
        except Exception: return set()
    def _cube_clear(self):
        """tira tudo do cubo sem disparar (fecha/limpa). ilo/ipu resetam; aqui so re-seleciona uma recipe vazia."""
        try: self._dispatch(6, argP=self._cube_recipe(1), timeout=1.0)   # volta pra SYNTHESIS (limpa alchemy)
        except Exception: pass
    def _currency(self, key):
        return None   # placeholder: resolvido no teste ao vivo (ler CurrencyManager). read_gold tolera None.
    def _synth_fuseable(self):
        """{(EItemSynthesisType, grade): qtd} dos itens que PODEM ir pra synthesis no Lv.65~80:
        grade <= teto do usuario (want[synth_maxgrade], default 2=rare -> NUNCA legendary+ por padrao) do
        tipo escolhido (want[synth_types], subconj de {0=Gear,1=Accessory,2=Material}), e Lv.61+ (o tier
        65~80 exige; material nao tem nivel). inv+bau (a synthesis usa 'Include Stash')."""
        maxg=self.want.get("synth_maxgrade",2)
        types=self.want.get("synth_types")
        if types is None: types={0,1,2}                                   # ausente=default todos; set() vazio=NENHUM (nao funde)
        c=collections.Counter()
        for _,key,uid in self._inv_index_items():
            info=self._item_info(key)
            if not info: continue
            _,grade,synth,lvl=info
            if grade is None or synth is None: continue
            if 0<=grade<=maxg and synth in types and (synth==2 or (lvl or 0)>=61):
                c[(synth,grade)]+=1
        return c
    def _do_synth(self):
        """AUTO-FUSE (logica validada ao vivo com o usuario). UMA fusao por chamada; o loop re-chama.
        Passos: conta os fundiveis; se algum TIPO tem >=9 de um grade C/U/R -> abre o cubo, poe em
        Synthesis+Lv.65~80, seleciona o tipo (Equipment 1o), AUTO-FILL, funde, e FECHA+REABRE (os itens
        voltam pro inv/stash -> o auto-stash cuida). Sem 9 de nada -> nao faz nada (loop so verifica).
        TETO/TIPOS (escolha do usuario, sincronizados via want): so funde grade <= want[synth_maxgrade] e
        tipo em want[synth_types]. _synth_fuseable ja filtra por isso; alem disso, DEPOIS do auto-fill leio
        o grade REALMENTE enchido (berp@sf+0xC8, que o auto-fill seta pro grade que escolheu) e, se passar
        do teto, NAO fundo (garantia dura de nunca consumir grade acima do escolhido). O auto-fill pega o
        MENOR grade com 9, entao com _synth_fuseable filtrado ele ja cai <= teto; a checagem do berp e o
        cinto-de-seguranca. Default teto=rare(2) -> nunca legendary+ (comportamento seguro de antes)."""
        if not all(self.sym.get(k) for k in ("ilo","imx","eby","uimgr_ti")): return False
        maxg=self.want.get("synth_maxgrade",2)
        types=self.want.get("synth_types")
        if types is None: types={0,1,2}                                    # ausente=default todos; set() vazio=NENHUM
        counts=self._synth_fuseable()
        blk=self.cache.get("synth_block",{}); now=time.time()
        tgt=None                                                            # 1o tipo PERMITIDO (nao bloqueado) com um grade <=teto >=9
        for t in sorted(types):
            if blk.get(t,0)>now: continue                                   # tipo deu over-cap ha pouco (auto-fill so acha grade>teto) -> nao reabrir (anti-livelock)
            if any(counts.get((t,g),0)>=9 for g in range(maxg+1)): tgt=t; break
        if tgt is None:
            return False                                                    # nada pra fundir (dado o teto/tipos/bloqueio) -> o loop segue so verificando
        if not self._open_cube(): return False
        sf=self._cube_sf()
        # Include-Stash ON (psd+0x60): sem isto o auto-fill so olha o INVENTARIO, e como o auto-stash ja
        # mandou os itens pro BAU, ele enchia <9 e nao fundia.
        try:
            psd=self.u64(self._resolve_bau()+self.sym.get("inv_psd_off",INV_PSD_OFF))
            cs=self.u64(psd+self.sym.get("psd_common_off",0x10))              # PlayerSaveData.commonSaveData
            if self.vptr(cs): self.wb(cs+self.sym.get("commonsave_usestorage",0x60),b"\x01")   # useStorage=Include-Stash (psd+0x60 era heroSaveDatas = BUG)
        except Exception: pass
        if not self._synth_set_lv6580(): self._close_cube(); return False   # Synthesis + Lv.65~80
        self._synth_set_type(tgt)                                          # tipo (Equipment/Accessory/Material)
        self._synth_set_lv6580()                                          # trocar o tipo RESETA o nivel -> re-por 65~80
        self.wb(sf+self.OFF["grade"], struct.pack("<i",0x7F))                          # SENTINELA no berp: se o auto-fill NAO reescrever (medo do review), fica >teto -> pula (fail-safe)
        self._dispatch(4); time.sleep(0.25)                               # AUTO FILL
        self._synth_set_lv6580()
        if self._cube_realn(sf)<9:                                        # se re-por o nivel esvaziou -> re-arma o sentinela e enche de novo
            self.wb(sf+self.OFF["grade"], struct.pack("<i",0x7F)); self._dispatch(4); time.sleep(0.25)
        n=self._cube_realn(sf)
        if n<9: self._close_cube(); return False                          # nao encheu 9 -> aborta
        fg=self.u32(sf+self.OFF["grade"])                                              # berp = grade REAL que o auto-fill enfiou (provado ao vivo 3x: ele reescreve com o grade posto, ate sobre o sentinela)
        if fg is None or not (0<=fg<=maxg):                               # TETO (fail-safe): so funde grade VALIDO 0..teto; sentinela(0x7F)/negativo/lixo/stale -> pula
            self.cache.setdefault("synth_block",{})[tgt]=time.time()+20   # anti-livelock: o auto-fill so acha grade>teto p/ este tipo -> nao reabrir por 20s
            self._close_cube(); return False
        GN=["common","uncommon","rare","legendary","immortal","arcana","beyond","celestial","divine","cosmic"]
        self._dispatch(5, timeout=2.0)                                     # SYNTHESIS (imx)
        for _ in range(200):                                               # espera a fusao terminar (busg@0x150 limpar = item gerado aparece)
            if not self._synth_busy(): break
            time.sleep(0.05)
        self.log("⚗️ Synthesis: %s %s (9 fundidos -> 1 de grade acima)"%(["Equipment","Accessory","Material"][tgt], GN[fg] if 0<=fg<len(GN) else fg))
        time.sleep(2.0)                                                    # DEIXA o item gerado aparecer e a animacao de cor da UI ASSENTAR antes de fechar;
        self._close_cube()                                                 # fechar rapido demais aqui bugava as cores (obs. do usuario). FECHA -> itens voltam pro inv/stash.
        return True                                                        # o proximo _do_synth reabre se tiver mais 9 (nao deixa aberto travando o auto-box)
    def _grade_of_key(self, key):
        """Grade REAL do item (via izb/ItemInfoData, cacheado). Fallback: digito da key."""
        if not key: return 99
        info=self._item_info(key)
        return info[1] if (info and info[1] is not None) else (key//1000)%10
    def _sort_grade_step(self, slottype):
        """UM passo de ordenacao por grade do container (2=stash / 1=inventario): conserta a 1a
        posicao fora de ordem (<=2 moves STASH->STASH via iw, funcao LEGIT). Amortizado no _auto_loop
        (so quando ocioso) -> nao bloqueia o auto-box. Retorna True se fez um move; False = ja ordenado.
        Ordena common->uncommon->rare->... packed nos slots de menor indice."""
        try:
            ra=self._ra()
            if not ra: return False
            bau=self._resolve_bau(); PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            if not self.vptr(PSD): return False
            master=self.u64(PSD+self.sym.get("inv_list_off",INV_LIST_OFF)); marr=self.u64(master+0x10); msz=self.u32(master+0x18)
            u2k={}
            for i in range(min(msz or 0,5000)):
                o=self.u64(marr+0x20+i*8)
                if o: u2k[self.u64(o+0x18)]=self.u32(o+0x10)
            off=self.sym.get("stash_off",0x90) if slottype==2 else self.sym.get("inv_slots_off",0x88)
            lp=self.u64(PSD+off); arr=self.u64(lp+0x10); sz=self.u32(lp+0x18) or 0
            uid={}; grade={}; unlocked=[]
            for i in range(min(sz,400)):
                o=self.u64(arr+0x20+i*8)
                if not o or not (self.rb(o+0x20,1) or b"\x00")[0]: continue   # so slots desbloqueados
                unlocked.append(i); u=self.u64(o+0x18)
                if u: uid[i]=u; grade[i]=self._grade_of_key(u2k.get(u,0))
            if len(uid)<2: return False
            want=[uid[s] for s in sorted(uid.keys(), key=lambda s:grade[s])]    # uids na ordem de grade
            targets=sorted(unlocked)[:len(uid)]                                 # os n menores slots
            empties=[i for i in unlocked if i not in uid]
            for idx,p in enumerate(targets):                                    # acha a 1a posicao errada
                if uid.get(p)==want[idx]: continue
                src=next((s for s,u in uid.items() if u==want[idx]), None)
                if src is None: return False
                if p in uid:                                                    # p ocupado por item errado -> tira pra um empty
                    if not empties: return False
                    self._dispatch(2, argP=ra, req=(slottype, p, slottype, empties[0], 1)); time.sleep(0.28)
                self._dispatch(2, argP=ra, req=(slottype, src, slottype, p, 1)); time.sleep(0.28)
                return True
            return False
        except Exception: return False
    def _inv_slot_has(self, idx, uid):
        """True se o slot de inventario 'idx' ainda contem o item 'uid' (pre-move check)."""
        try:
            bau=self._resolve_bau(); PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            lp=self.u64(PSD+self.sym.get("inv_slots_off",0x88)); arr=self.u64(lp+0x10); sz=self.u32(lp+0x18)
            for i in range(min(sz or 0,300)):
                o=self.u64(arr+0x20+i*8)
                if o and self.u32(o+0x10)==idx: return self.u64(o+0x18)==uid
        except Exception: pass
        return False
    def _slot_has(self, slottype, idx, uid):
        """True se o slot (1=inv / 2=stash) no INDEX 'idx' ainda contem o item 'uid'. Pre-ioa check."""
        try:
            bau=self._resolve_bau(); PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            off=self.sym.get("inv_slots_off",0x88) if slottype==1 else self.sym.get("stash_off",0x90)
            lp=self.u64(PSD+off); arr=self.u64(lp+0x10)
            o=self.u64(arr+0x20+idx*8)
            return bool(o and self.u64(o+0x18)==uid and (self.rb(o+0x20,1) or b"\x00")[0])
        except Exception: return False
    def _locate_grade(self, want_t, want_g):
        """Lista (slottype 1=inv/2=stash, index, uid, key) dos itens de (EItemSynthesisType, grade),
        inv+stash. P/ colocar os 9 manualmente no cubo via ioa. Ignora soulstone."""
        try:
            bau=self._resolve_bau(); PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            m=self.u64(PSD+self.sym.get("inv_list_off",INV_LIST_OFF)); marr=self.u64(m+0x10); msz=self.u32(m+0x18)
            u2k={}
            for i in range(min(msz or 0,5000)):
                o=self.u64(marr+0x20+i*8)
                if o: u2k[self.u64(o+0x18)]=self.u32(o+0x10)
            out=[]
            for st,off in ((1,self.sym.get("inv_slots_off",0x88)),(2,self.sym.get("stash_off",0x90))):
                lp=self.u64(PSD+off); arr=self.u64(lp+0x10); sz=self.u32(lp+0x18)
                for i in range(min(sz or 0,4000)):
                    o=self.u64(arr+0x20+i*8)
                    if not o or not (self.rb(o+0x20,1) or b"\x00")[0]: continue
                    uid=self.u64(o+0x18); k=u2k.get(uid,0)
                    if not k or (190001<=k<=190010): continue
                    fam=k//100000; g=(k//1000)%10
                    tt=0 if fam in(3,4) else 1 if fam in(5,6) else 2 if fam==1 else None
                    if tt==want_t and g==want_g: out.append((st,i,uid,k))
            return out
        except Exception: return []
    # --- SYNTHESIS (cubo): manual via ioa. inf(ux)->ilo(tipo)->ioa x9->imx. uw.Cube static ---
    def _cube_sf(self):
        cs=self.sym.get("cube_slot")
        if not cs: return None
        k=self.u64(self.base+cs); return self.u64(k+STATIC_FIELDS_OFF) if self.vptr(k) else None
    CUBE_LEVEL_OFF=0x1CC; CUBE_MAX_LEVEL=100          # uw.Cube.bese (ObscuredInt) = nivel RUNTIME do cubo
    def cube_level(self):
        """Nivel atual do CUBO (ObscuredInt bese @ _cube_sf()+0x1CC). None se o cubo nao resolveu."""
        sf=self._cube_sf()
        return self._obs_int(sf+self.CUBE_LEVEL_OFF) if sf else None
    def set_cube_level(self, level=None):
        """Sobe o nivel do CUBO (runtime ObscuredInt bese) p/ liberar a sintese de itens 65~80.
        O nivel indexa a lista de recipes (bam.mey): cubo baixo NAO enxerga o tier 65+. So o runtime conta —
        o save int (cubeSaveLevelData) nao tem xref no codigo e re-serializa por cima, entao nao mexo nele.
        AVISO (provado ao vivo): como qualquer ObscuredInt, o honesty-check periodico do ACTk (~12s) fecha o
        jogo (Application.Quit; NOPar ynj NAO impede). MAS o valor PERSISTE (auto-save) e recarrega LIMPO —
        reabra e o cubo estara no nivel novo (validado: 100 carrega liso). Retorna (ok, level)."""
        level=self.CUBE_MAX_LEVEL if level is None else int(level)
        sf=self._cube_sf()
        if not sf: return False, level
        return self._obs_set(sf+self.CUBE_LEVEL_OFF, level), level
    def _synth_busy(self):
        sf=self._cube_sf()
        if not sf: return True
        b=self.rb(sf+self.OFF["busy"],1); return (not b) or b[0]!=0        # [model+0x150]!=0 = ocupado
    def _synth_ux(self):
        """Ponteiro do objeto-recipe 'ux' de SYNTHESIS (bero[1] em Dict<ERecipeType,ux>@MODEL+0xF0).
        imx roteia por bery.jmm(); preciso setar bery=esse ux (via inf) pra ir pra synthesis."""
        sf=self._cube_sf()
        if not sf: return 0
        bero=self.u64(sf+self.OFF["beru"])
        if not self.vptr(bero): return 0
        ent=self.u64(bero+0x18); cnt=self.u32(bero+0x20)
        if not ent or cnt is None: return 0
        for i in range(min(cnt,32)):
            e=ent+0x20+i*0x18
            if self.u32(e+8)==1: return self.u64(e+0x10)     # key==SYNTHESIS(1) -> value=ux
        return 0
    def _cube_filled(self):
        """Nº de ENTRADAS na List<CubeInData> @ model+0x100 (= slots, nao itens reais)."""
        sf=self._cube_sf()
        if not sf: return 0
        lst=self.u64(sf+self.OFF["inlist"])
        return (self.u32(lst+0x18) or 0) if self.vptr(lst) else 0
    def _s32(self, a):
        x=self.rb(a,4)
        if not x: return 0
        v=int.from_bytes(x,"little"); return v-0x100000000 if v>=0x80000000 else v
    def _synth_result_lvl(self, sf=None):
        """(MinResultLevel, MaxResultLevel) do recipe atual (besu@0x240). Result level da fusao.
        Recipe.MinResultLevel@0x3C, MaxResultLevel@0x40. (0,0) se sem recipe."""
        sf=sf or self._cube_sf()
        besu=self.u64(sf+self.OFF["recipe"]) if sf else 0
        if not self.vptr(besu): return (0,0)
        return (self._s32(besu+0x3c), self._s32(besu+0x40))
    def _cube_realn(self, sf=None):
        """Nº de itens REAIS no cubo = CubeInData com bfbk@0x18 (CubeItemData) != null.
        (slot vazio tem CubeDataChange@0x10 nao-nulo mas bfbk@0x18 nulo -> nao contar +0x10!)"""
        sf=sf or self._cube_sf()
        if not sf: return 0
        lst=self.u64(sf+self.OFF["inlist"])
        if not self.vptr(lst): return 0
        n=self.u32(lst+0x18) or 0; arr=self.u64(lst+0x10); c=0
        for i in range(min(n,12)):
            ent=self.u64(arr+0x20+i*8)
            if ent and self.u64(ent+0x18): c+=1
        return c
    def _item_info(self, key):
        """(EItemType, EGradeType, EItemSynthesisType, Level) REAIS da ItemInfoData via izb, CACHEADO
        (as defs sao estaticas). itemtype:1=MATERIAL,2=GEAR. synthtype:0=Gear,1=Accessory,2=Material.
        grade:0=common..3=legendary,4=immortal+. level: nivel do item (65/80...). None se izb ausente.
        izb e getter PURO -> seguro via _remote_call (provado, jogo vivo)."""
        cache=self.cache.setdefault("iteminfo", {})
        if key in cache: return cache[key]
        izb=self.sym.get("izb")
        if not izb: cache[key]=None; return None
        try:
            p=self._remote_call(self.base+izb, [int(key)])
            if not p or not self.vptr(p): cache[key]=None; return None
            info=(self.u32(p+self.OFF["it_type"]), self.u32(p+self.OFF["it_grade"]), self.u32(p+self.OFF["it_synth"]), self._s32(p+self.OFF["it_level"]))
            g,st=info[1],info[2]
            if g is None or g>10 or st is None or st>3: info=None      # valida
            cache[key]=info; return info
        except Exception: cache[key]=None; return None
    def _synth_ready_at_level(self, stype, lo, hi):
        """True se ha >=9 itens de UM grade fundivel (g<=3) do EItemSynthesisType 'stype' com nivel
        em [lo,hi] (inv+stash), via izb (tipo/grade/nivel REAIS). So assim vale a pena dar ipu — evita
        ipu constante estressando o cubo e so funde quando ha material 65-80 do tipo aberto."""
        try:
            bau=self._resolve_bau(); PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            if not self.vptr(PSD): return False
            master=self.u64(PSD+self.sym.get("inv_list_off",INV_LIST_OFF))
            marr=self.u64(master+0x10); msz=self.u32(master+0x18); u2k={}
            for i in range(min(msz or 0,5000)):
                o=self.u64(marr+0x20+i*8)
                if o: u2k[self.u64(o+0x18)]=self.u32(o+0x10)
            cnt=collections.Counter()
            for off in (self.sym.get("inv_slots_off",0x88), self.sym.get("stash_off",0x90)):
                lp=self.u64(PSD+off); arr=self.u64(lp+0x10); sz=self.u32(lp+0x18)
                for i in range(min(sz or 0,4000)):
                    o=self.u64(arr+0x20+i*8)
                    if not o: continue
                    uid=self.u64(o+0x18)
                    if not uid: continue
                    k=u2k.get(uid,0)
                    if not k or (190001<=k<=190010): continue
                    info=self._item_info(k)
                    if info and info[2]==stype and (info[1] is not None and info[1]<=3) and lo<=(info[3] or 0)<=hi:
                        cnt[info[1]]+=1
            return any(n>=9 for n in cnt.values())
        except Exception: return False
    def _items_by_grp(self, stash=True, inv=True):
        """Counter{(EItemSynthesisType, grade): qtd} usando a DEFINICAO REAL do jogo (izb -> ItemInfoData),
        NAO chute por familia (o chute misturava Gear/Accessory e contava errado). Fallback: familia."""
        try:
            bau=self._resolve_bau(); PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            if not self.vptr(PSD): return None
            master=self.u64(PSD+self.sym.get("inv_list_off",INV_LIST_OFF))
            marr=self.u64(master+0x10); msz=self.u32(master+0x18); u2k={}
            for i in range(min(msz or 0,4000)):
                o=self.u64(marr+0x20+i*8)
                if o: u2k[self.u64(o+0x18)]=self.u32(o+0x10)
            grp=collections.Counter()
            offs=([self.sym.get("inv_slots_off",0x88)] if inv else [])+([self.sym.get("stash_off",0x90)] if stash else [])
            for off in offs:
                lp=self.u64(PSD+off); arr=self.u64(lp+0x10); sz=self.u32(lp+0x18)
                for i in range(min(sz or 0,4000)):
                    o=self.u64(arr+0x20+i*8)
                    if not o or not (self.rb(o+0x20,1) or b"\x00")[0]: continue
                    k=u2k.get(self.u64(o+0x18),0)
                    if not k or (190001<=k<=190010): continue    # ignora soulstone
                    info=self._item_info(k)
                    if info and info[2] is not None and info[2]<=2:
                        grp[(info[2], info[1])]+=1               # (synthtype REAL, grade REAL)
                    elif not info:
                        fam=k//100000; g=(k//1000)%10            # fallback sem izb: chute por familia
                        t=0 if fam in (3,4) else 1 if fam in (5,6) else 2 if fam==1 else None
                        if t is not None: grp[(t,g)]+=1
            return grp
        except Exception: return None
    def _synth_type_ready(self):
        """Retorna um EItemSynthesisType (0=Gear,1=Accessory,2=Material) que tem >=9 de um grade
        FUNDIVEL (common..legendary, sobe ate IMMORTAL) no inv+stash. None se nada pronto."""
        ts=self._synth_ready_types()
        return ts[0] if ts else None
    def _synth_ready_types(self, grp=None):
        """Lista de EItemSynthesisType que tem >=9 de ALGUM grade FUNDIVEL (g<=3, para em immortal).
        Ordena por maior grade fundivel disponivel (o ipu auto-pega o grade mais alto com >=9)."""
        if grp is None: grp=self._items_by_grp(stash=True, inv=True)
        if not grp: return []
        best={}                                                 # tipo -> maior grade g<=3 com >=9
        for (t,g),n in grp.items():
            if g<=3 and n>=9: best[t]=max(best.get(t,-1), g)
        return [t for t,_ in sorted(best.items(), key=lambda kv:-kv[1])]
    def _master_item_count(self):
        """Nº total de itemSaveDatas (master). Cai ~8 por fusao (9 consumidos, 1 criado). Barato."""
        try:
            bau=self._resolve_bau(); PSD=self.u64(bau+self.sym.get("inv_psd_off",INV_PSD_OFF))
            master=self.u64(PSD+self.sym.get("inv_list_off",INV_LIST_OFF))
            return self.u32(master+0x18) or 0
        except Exception: return 0

    # ---- tick (mantem tudo aplicado) ----
    def tick(self):
        with self.lock:
            if (not self.pm or not self._proc_alive()) and not self.attach():   # jogo reiniciou -> re-attach sozinho
                return False
            if self._wd_hold:                     # watchdog reiniciando o jogo: attacha so, sem aplicar nada (start LIMPO)
                return True
            try:
                self.apply_actk(); self.apply_god(); self.apply_hitkill()
                self.apply_stats(); self.apply_stage(); self.apply_speedhack(); self.apply_automation()
                return True
            except Exception:
                self.pm=None; self.pid=None; return False
