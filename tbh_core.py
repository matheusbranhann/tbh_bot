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
import os, sys, struct, subprocess, hashlib, json, re, time, ctypes, collections, difflib, threading
from ctypes import wintypes

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
def _find_game_dir():
    """Pasta REAL do jogo (onde vive GameAssembly.dll). BUG CORRIGIDO 2026-07-11: no exe congelado
    HERE=dist/ -> a DLL esta no PAI, nao em HERE -> dll_hash falhava -> offsets errados/vazios ->
    inventario 0 e caixa nao abria. Agora procura: HERE, pai de HERE, cwd, caminho Steam."""
    cands=[HERE, os.path.dirname(HERE), os.getcwd(),
           r"d:\SteamLibrary\steamapps\common\TaskbarHero"]
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
DUMPER=P("tools","Il2CppDumper","Il2CppDumper.exe")
CACHE=P("cache")
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
            if mm: out["iuw"]=int(mm.group(1),16)
    # imx=uw.Cube trigger synthesis/craft: <TriggerCurrentRecipeLogic> (nome preservado no state machine)
    mm=re.search(r'\[AsyncStateMachine\(typeof\(uw\.Cube\.<TriggerCurrentRecipeLogic>[^\n]*\n\s*// RVA: (0x[0-9A-Fa-f]+)',src)
    if mm: out["imx"]=int(mm.group(1),16)
    # === SYNTHESIS (uw.Cube): ancora na classe 'Cube' (nome estavel) + assinaturas UNICAS dos metodos ===
    mcube=re.search(r'public static class \S*\bCube\b',src)
    if mcube:
        seg=src[mcube.start():mcube.start()+95000]
        def _rb(sig):   # RVA da linha 'RVA:' imediatamente antes de uma assinatura
            m=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*'+sig,seg)
            return int(m.group(1),16) if m else None
        out["ilo"]=_rb(r'public static void \w+\(EItemSynthesisType a\)')      # set tipo
        out["ili"]=_rb(r'public static bool \w+\(EGradeType a\)')             # set grade (evento)
        out["inf"]=_rb(r'private static void \w+\(ux a\)')                    # set recipe (bery)
        out["ima"]=_rb(r'public static bool \w+\(ux a\)')                     # set variante de nivel
        out["iog"]=_rb(r'private static void \w+\(int a, CubeInData b\)')     # builder recipe-grade
        out["ioa"]=_rb(r'public static EAddCubeResult \w+\(ESlotType a, int b\)')  # add item ao cubo
        # ipu = 'public static void X()' IMEDIATAMENTE antes de ipv (assinatura unica: retorna BucketCountResult)
        mp=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*public static void \w+\(\) \{ \}\s*\n\s*// RVA:[^\n]*\n\s*private static \S*BucketCountResult',seg)
        if mp: out["ipu"]=int(mp.group(1),16)
    # === MOVE (auto-stash): ra.iw(MoveRequest, Action<...>) — MoveRequest e assinatura distintiva ===
    miw=re.search(r'RVA: (0x[0-9A-Fa-f]+)[^\n]*\n\s*public \w+ \w+\(MoveRequest a, Action<\w+> b\)',src)
    if miw: out["iw"]=int(miw.group(1),16)
    # === offsets de inventario/stash em PlayerSaveData (shiftam entre builds) ===
    mio=re.search(r'List<InventorySaveData> \w+; // (0x[0-9A-Fa-f]+)',src)
    if mio: out["inv_slots_off"]=int(mio.group(1),16)
    mso=re.search(r'List<StashSaveData> \w+; // (0x[0-9A-Fa-f]+)',src)
    if mso: out["stash_off"]=int(mso.group(1),16)
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

def resolve_symbols(log=lambda m:None):
    """Retorna {gra,bau_ti,ynj} pra build atual. Auto re-dumpa se o jogo atualizou."""
    os.makedirs(CACHE,exist_ok=True)
    _best=lambda: dict(max(KNOWN_BUILDS.values(), key=len))   # build com MAIS offsets = mais completo/recente
    h=dll_hash()
    if not h: return _best()                                  # sem DLL: chuta o mais completo (nao o 1o/antigo)
    if h in KNOWN_BUILDS:
        return dict(KNOWN_BUILDS[h])
    cf=os.path.join(CACHE,"offsets_%s.json"%h)
    if os.path.exists(cf):
        try: return json.load(open(cf))
        except Exception: pass
    log("Game updated (build %s) — re-dumping offsets automatically… (~30s)"%h)
    got=_redump()
    if got.get("gra") or got.get("ynj"):        # extracao OK (bau_ti pode ser None = resolve em runtime)
        try: json.dump(got,open(cf,"w"))
        except Exception: pass
        log("New offsets resolved and saved (build %s)."%h)
    else:
        log("Re-dump failed (%s). Using last known."%got.get("error","?"))
        got=_best()
    return got

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
        self.want={"actk":False,"god":False,"hitkill":False,"autobox":False,"autoitem":False,"autosynth":False}
        self.stats={}       # nome -> valor (float) a forcar (manual)
        self.speed_stats={} # (legado, nao usado)
        self.stage={}       # campo -> int a forcar
        self.sh_mult=1.0    # SPEEDHACK de relogio desejado (1.0 = off)
        self.sh=None        # estado do hook instalado
        self.abx=None       # estado do dispatcher generico (InputManager.Update)
        self._abx_thr=None  # (legado)
        self._itm_thr=None  # (legado)
        self._auto_thr=None # thread UNICA do loop de automacao (caixa->stash->fuse)
        self._disp_lock=threading.RLock()  # serializa comandos no dispatcher
        self._rc_lock=threading.RLock()    # serializa _remote_call (scratch compartilhado)
    # ---- attach ----
    def attach(self):
        try:
            import pymem, pymem.process
            pm=pymem.Pymem(PROC)
            ga=pymem.process.module_from_name(pm.process_handle,MOD)
            if ga and ga.lpBaseOfDll:
                if pm.process_id!=self.pid:
                    self.pid=pm.process_id; self.cache={}; self.sh=None; self.abx=None   # hooks morreram com o processo antigo
                    self.sym=resolve_symbols(self.log)
                    self.log("attach PID=%d base=%#x"%(pm.process_id,ga.lpBaseOfDll))
                self.pm=pm; self.base=ga.lpBaseOfDll; self.size=ga.SizeOfImage
                if not self.sym.get("cube_slot") and self.sym.get("ilo"):   # build novo: resolve cube_slot ao vivo
                    cs=self._resolve_cube_slot()
                    if cs: self.sym["cube_slot"]=cs; self.log("cube_slot resolved live: %#x"%cs)
                return True
        except Exception:
            self.pm=None; self.pid=None
        return False
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
    def _find_stageboxes(self):
        """{EBoxType: ptr} das instancias REAIS de StageBox (validadas)."""
        cur=self.cache.get("sb_inst")
        if cur and all(self.u64(a)==cur["_k"] for a in cur.values() if a!=cur.get("_k")):
            pass  # (validacao real abaixo)
        klass=self._stagebox_klass()
        if not klass: return {}
        # reusa cache se os ponteiros ainda apontam pra klass
        cc=self.cache.get("sb_inst")
        if cc and cc.get("_k")==klass and cc.get("map") and all(self.u64(a)==klass for a in cc["map"].values()):
            return cc["map"]
        kp=struct.pack("<Q",klass); found={}
        for base,size in self._mem_regions((0x04,0x40)):
            d=self.rb(base,size)
            if not d: continue
            j=d.find(kp)
            while j>=0:
                if j%8==0:
                    a=base+j; bt=self.u32(a+0x38); btn=self.u64(a+0x58)
                    hb=self.rb(a+0x128,1); bz=self.rb(a+0xa0,1)
                    if bt in (0,1,2) and hb and hb[0]<=1 and bz and bz[0]<=1 and btn and 0x10000000000<=btn<0x7f0000000000 and self.u64(btn)!=klass:
                        found.setdefault(bt,a)
                j=d.find(kp,j+8)
            if len(found)>=3: break
        self.cache["sb_inst"]={"_k":klass,"map":found}
        return found
    # === DISPATCHER GENERICO (um hook em InputManager.Update, varios comandos main-thread) ===
    # data @cave+0x800: en@0 doFlag@1 inop@2 cmd@4 argP@8 argI@0x10 cnt@0x14
    # cmds: 1=box llx(argP,0) · 2=stash igj(argP=ra,INVENTORY,argI,null,STASH) ·
    #       3=synth ilo(argI) · 4=synth ipu() · 5=synth imx()
    def _dispatch_code(self, cave, back_va):
        s=self.sym; B=self.base
        LLX=B+s.get("llx",0); IW=B+s.get("iw",0); ILO=B+s.get("ilo",0); IPU=B+s.get("ipu",0); IMX=B+s.get("imx",0); INF=B+s.get("inf",0); ILI=B+s.get("ili",0); IOG=B+s.get("iog",0); IOA=B+s.get("ioa",0); IMA=B+s.get("ima",0); LLM=B+s.get("llm",0)
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
        L("c11"); c+=b"\x83\xF8\x0B"; jcc(b"\x0F\x85","c12")
        c+=b"\x48\xB8"+imm(D_ARGP)+b"\x48\x8B\x08\x48\x85\xC9"; jcc(b"\x0F\x84","done")   # rcx=[argP]=box; je done
        c+=b"\x48\x83\xEC\x20\x48\xB8"+imm(LLM)+b"\xFF\xD0\x48\x83\xC4\x20"; jmp("done")  # llm()
        # cmd12 CALL GENERICO: [D_FUNC](rcx=[argP], edx=[argI]). Retorno em D_RET. P/ clear do cubo (inl/inm) etc.
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
        c+=b"\x40\x53\x48\x83\xEC\x20"                                 # stolen: push rbx; sub rsp,0x20
        c+=b"\xE9"; rbp=len(c); c+=b"\x00\x00\x00\x00"
        c[rbp:rbp+4]=struct.pack("<i", back_va-(cave+rbp+4))
        for pos,l in fix: c[pos:pos+4]=struct.pack("<i", lab[l]-(pos+4))
        return bytes(c)
    def _install_dispatch(self):
        upd=self.sym.get("upd")
        if not upd or not self.sym.get("llx"): self.log("dispatcher: offset upd/llx missing"); return False
        UPD=self.base+upd; stolen=self.rb(UPD,6); KNOWN=b"\x40\x53\x48\x83\xEC\x20"
        if not stolen or len(stolen)!=6: return False
        if stolen!=KNOWN:
            if stolen[0]==0xE9:                                # hook stray de sessao morta -> restaura o prologo e reinstala
                hs=self._suspend_all()
                try: self.wb(UPD,KNOWN)
                finally: self._resume_all(hs)
                stolen=KNOWN; self.log("dispatcher: stray hook removed, reinstalling")
            else:
                self.log("dispatcher: unexpected InputManager.Update prologue (%s)"%stolen.hex()); return False
        cave=self.alloc_cave(UPD)
        if not cave: self.log("dispatcher: cave failed"); return False
        self.wb(cave+0x800,b"\x00"*0x50); self.wb(cave,self._dispatch_code(cave,UPD+6))
        self.wb(cave+0x900,b"\xC3")                            # ret p/ o fake delegate
        self.wb(cave+0x920,b"\x00"*0x48); self.wb(cave+0x920+0x18,struct.pack("<Q",cave+0x900))  # fake[+0x18]=ret
        patch=b"\xE9"+struct.pack("<i",cave-(UPD+5))+b"\x90"
        hs=self._suspend_all()
        try: self.wb(UPD,patch)
        finally: self._resume_all(hs)
        self.wb(cave+0x800,b"\x01")                            # en
        self.abx={"cave":cave,"data":cave+0x800,"upd":UPD,"stolen":bytes(stolen)}
        self.log("Dispatcher installed @ %#x"%cave); return True
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
        klass=self._klass_by_name("","ra")
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
        need=self.want.get("autobox") or item
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
        while (W.get("autobox") or W.get("autoitem") or W.get("autosynth")) and self.abx and self.pm:
            try:
                did=False
                if W.get("autobox") and self._do_autobox(): did=True          # 1) caixa: prioridade maxima
                if (W.get("autoitem") or W.get("autosynth")) and self._do_stash(used): did=True   # 2) stash
                if W.get("autosynth") and not did and self._do_synth(): did=True # 3) fuse (so se nao houve caixa/stash pendente)
                if W.get("autoitem") and not did and self._sort_grade_step(2): did=True  # 4) ordena o stash por grade (so quando ocioso)
                time.sleep(0.12 if did else 0.5)
            except Exception: time.sleep(0.3)
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
                if self.u64(tgt)==klass: self._dispatch(1, argP=tgt)
            return False
        NAMES={0:"NORMAL",1:"BOSS",2:"ACTBOSS"}; opened=False
        for t in (0,1,2):
            tgt=boxes.get(t)
            if not tgt: continue
            cnt=self._iuw_count(t); guard=0
            while cnt and cnt>0 and guard<15 and self.want.get("autobox") and self.abx:
                if self.u64(tgt)!=klass: break                               # ponteiro morreu (heap moveu)
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
    def _do_synth(self):
        """Uma tentativa de SYNTHESIS 65-80 SEGURA. Retorna True se fundiu. *** NUNCA funde nivel baixo ***.
        NAO toca inf/ilo (resetariam o nivel pra 1-10 e queimariam itens). O user deixa o Cubo aberto no
        TIPO + Lv.65~80; o bot so da `ipu` (respeita o dropdown) e `imx` se o resultado for >= 65."""
        sf=self._cube_sf()
        if not sf or self._synth_busy(): return False                        # cubo fechado/ocupado
        if not self.vptr(self.u64(sf+0x140)): return False                   # cubo nao pronto (recipe vazio)
        mn,mx=self._synth_result_lvl(sf)
        if (mx or 0)<SYNTH_MIN_LVL: return False                             # dropdown nao esta em 65-80 -> protege itens
        stype=self.u32(sf+0x254)                                             # besx = tipo aberto
        if not self._synth_ready_at_level(stype, mn or SYNTH_MIN_LVL, mx): return False  # so age se ha >=9 de um grade 65-80
        psd=self.u64(self._resolve_bau()+self.sym.get("inv_psd_off",INV_PSD_OFF))
        if self.vptr(psd): self.wb(psd+0x60,b"\x01")                        # Include-Stash ON
        self._dispatch(4); time.sleep(0.28)                                 # ipu: full-fill do tipo+nivel do user
        _,maxrl=self._synth_result_lvl(sf)
        if self.rb(sf+0x229,1)==b"\x01" and self._cube_realn(sf)>=9 and (maxrl or 0)>=SYNTH_MIN_LVL:
            self._dispatch(5)                                               # imx: funde 9->1 (resultado 65+)
            for _ in range(200):
                if not self._synth_busy(): break
                time.sleep(0.05)
            self.log("⚗️ Synthesis: fused 9 (result Lv.%d)."%maxrl)
            return True
        return False
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
    def _synth_busy(self):
        sf=self._cube_sf()
        if not sf: return True
        b=self.rb(sf+0x150,1); return (not b) or b[0]!=0        # [model+0x150]!=0 = ocupado
    def _synth_ux(self):
        """Ponteiro do objeto-recipe 'ux' de SYNTHESIS (bero[1] em Dict<ERecipeType,ux>@MODEL+0xF0).
        imx roteia por bery.jmm(); preciso setar bery=esse ux (via inf) pra ir pra synthesis."""
        sf=self._cube_sf()
        if not sf: return 0
        bero=self.u64(sf+0xF0)
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
        lst=self.u64(sf+0x100)
        return (self.u32(lst+0x18) or 0) if self.vptr(lst) else 0
    def _s32(self, a):
        x=self.rb(a,4)
        if not x: return 0
        v=int.from_bytes(x,"little"); return v-0x100000000 if v>=0x80000000 else v
    def _synth_result_lvl(self, sf=None):
        """(MinResultLevel, MaxResultLevel) do recipe atual (besu@0x240). Result level da fusao.
        Recipe.MinResultLevel@0x3C, MaxResultLevel@0x40. (0,0) se sem recipe."""
        sf=sf or self._cube_sf()
        besu=self.u64(sf+0x240) if sf else 0
        if not self.vptr(besu): return (0,0)
        return (self._s32(besu+0x3c), self._s32(besu+0x40))
    def _cube_realn(self, sf=None):
        """Nº de itens REAIS no cubo = CubeInData com bfbk@0x18 (CubeItemData) != null.
        (slot vazio tem CubeDataChange@0x10 nao-nulo mas bfbk@0x18 nulo -> nao contar +0x10!)"""
        sf=sf or self._cube_sf()
        if not sf: return 0
        lst=self.u64(sf+0x100)
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
            info=(self.u32(p+0x34), self.u32(p+0x38), self.u32(p+0x48), self._s32(p+0x6C))
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
            try:
                self.apply_actk(); self.apply_god(); self.apply_hitkill()
                self.apply_stats(); self.apply_stage(); self.apply_speedhack(); self.apply_automation()
                return True
            except Exception:
                self.pm=None; self.pid=None; return False
