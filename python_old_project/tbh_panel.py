#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
tbh_bot — single control panel for TaskBarHero (CustomTkinter UI).
Tabs: Trainer (protection + stats by category + stage + PROFILES), Inventory, Market.
+ Price overlay. Auto-offsets: re-resolves itself when the game updates.
Run:  python tbh_panel.py   (or TBH.bat)
Deps: customtkinter, pymem, winsdk (overlay). Engine in tbh_core.py.
"""
import os, sys, json, threading, subprocess, time, struct
import tkinter as tk
from tkinter import ttk
import customtkinter as ctk
import tbh_core as C

HERE=C.HERE
# ---- palette — "precision instrument": deep black, hairlines, mono accents; single orange trace ----
W_BG="#050506"                     # deep black ground
SURF="#0c0d0f"                     # bars / lowest surface
CARD="#16181c"                     # cards
CARD2="#212429"                    # inputs / rows / raised
STROKE="#2b2e34"                   # hairline border
STROKE2="#3d4149"                  # stronger border
FG="#ffffff"; SUB="#9aa0ab"; SUBTLE="#666b74"
ACC="#ff7a18"; ACC_H="#ff9440"; ACC_TXT="#0a0a0a"   # orange accent (replaces the green/volt)
GRN=ACC                            # every former "green/good" now reads as the orange trace
RED="#f87171"; BLUE="#60a5fa"; AMBER="#fbbf24"      # semantic: danger / info / warning
GREENBG="#3a2410"                  # dark orange-brown (former green fill)

PROFILES_FILE=C.P("profiles.json")
MAXP={"Attack Damage":1e9,"Attack Speed":50,"Critical Chance":100,"Critical Damage":1000,
      "Cooldown Reduction":90,"Cast Speed":50,"Movement Speed":50,"Max Hp":1e7,"Armor":1e6,
      "Physical Damage":1e6,"Fire Damage":1e6,"Cold Damage":1e6,"Lightning Damage":1e6,
      "Chaos Damage":1e6,"Area of Effect %":10,"Area of Effect Damage":10,"Life Leech":100,
      "Dmg Reduction":90,"All Element Resistance":100}
STAT_GROUPS=[
 ("⚔  ATTACK",["Attack Damage","Attack Speed","Critical Chance","Critical Damage",
               "Cooldown Reduction","Cast Speed","Physical Damage","Fire Damage",
               "Cold Damage","Lightning Damage","Chaos Damage"]),
 ("🛡  DEFENSE",["Max Hp","Armor","Dodge Chance","Block Chance","All Element Resistance",
               "Hp Regen /Sec","Dmg Absorption","Dmg Reduction"]),
 ("✦  OTHER",["Movement Speed","Area of Effect %","Area of Effect Damage",
               "Add HP/Kill","Life Leech","Skill Heal"]),
]
STAGE_SHOW=["Act","StageNo","WaveAmount","WaveMonsterAmount","MonsterDropItemRate",
            "BossDropItemRate","BossDropItemKey","BossHpMultiplier","BossGoldMultiplier",
            "BossExpMultiplier","SoulStoneAmount"]

ctk.set_appearance_mode("dark")
def F(size,w="normal"): return ctk.CTkFont("Segoe UI",size,w)
def MONO(size): return ctk.CTkFont("Consolas",size)

def self_launcher():
    # comando pra relancar a mim mesmo (.exe congelado ou python + script)
    return [sys.executable] if getattr(sys,"frozen",False) else [sys.executable, os.path.abspath(__file__)]

class Panel:
    def __init__(self, root):
        self.root=root; root.title("tbh_bot"); root.configure(fg_color=W_BG)
        # tamanho pela TELA, nao fixo em 880x920: numa tela alta (ex 1440p) o painel nascia pequeno
        # e obrigava a rolar/esticar pra ver o que ja cabia. Limita a 96% da area util e nunca passa
        # do tamanho do conteudo.
        self._W=920; self._H=max(700, min(1060, root.winfo_screenheight()-140))
        root.geometry("%dx%d"%(self._W,self._H)); root.minsize(860,700); root.attributes("-topmost",True)
        self.eng=C.Engine(log=self.log); self.prices=C.Prices(); self.overlay_proc=None
        self.stat_vars={}; self.stage_vars={}; self._logbuf=[]
        self._was_conn=False
        self._style_tree()
        self._splash_show()                       # tela de loading enquanto monta (nao carrega "na cara")
        self.root.after(30, self._run_build)

    def _splash_show(self):
        # Full-size CURTAIN over the panel's footprint: it hides the panel while every widget paints
        # behind it, and only lifts once everything is rendered. No black/white flash, no pop-in.
        self.root.withdraw()
        self._geo=(self._W,self._H)
        w,h=self._geo; self._pos=((self.root.winfo_screenwidth()-w)//2,(self.root.winfo_screenheight()-h)//2)
        sp=ctk.CTkToplevel(self.root); sp.overrideredirect(True); sp.attributes("-topmost",True)
        sp.configure(fg_color=W_BG)
        cw,ch=w+18,h+64                                   # a touch larger than the panel so it fully covers it
        sp.geometry("%dx%d+%d+%d"%(cw,ch,self._pos[0]-9,self._pos[1]-34))
        fr=ctk.CTkFrame(sp,fg_color=W_BG,corner_radius=0,border_width=1,border_color=STROKE); fr.pack(fill="both",expand=True)
        box=ctk.CTkFrame(fr,fg_color="transparent"); box.place(relx=0.5,rely=0.5,anchor="center")
        brand=ctk.CTkFrame(box,fg_color="transparent"); brand.pack()
        ctk.CTkLabel(brand,text="tbh_bot",text_color=FG,font=MONO(30)).pack(side="left")
        ctk.CTkLabel(brand,text="◦",text_color=ACC,font=MONO(30)).pack(side="left",padx=(8,0))
        self._sp_lbl=ctk.CTkLabel(box,text="loading…",text_color=SUB,font=MONO(12)); self._sp_lbl.pack(pady=(10,14))
        pb=ctk.CTkProgressBar(box,mode="indeterminate",width=260,progress_color=ACC,fg_color=CARD2,height=4); pb.pack(); pb.start()
        self.splash=sp; sp.update()
    def _splash_step(self,txt):
        try: self._sp_lbl.configure(text=txt); self.splash.update()
        except Exception: pass
    def _run_build(self):
        # Build + paint under the curtain, then do the SLOW work (connect + read inventory) on a
        # BACKGROUND thread so the loading bar keeps animating perfectly smooth. The main thread only
        # spins the animation until the data is ready, then renders it (fast) and drops the curtain.
        self._boot=None
        try:
            w,h=self._geo
            self.root.geometry("%dx%d+%d+%d"%(w,h,self._pos[0],self._pos[1]))
            self._build()
            self.root.deiconify()
            try: self.splash.lift(); self.splash.attributes("-topmost",True)   # keep curtain above the panel
            except Exception: pass
            for t in ("Inventory","Market","Trainer"):                          # paint each tab's canvas for real
                self._splash_step("rendering %s…"%t); self.tabs.set(t)
                for _ in range(4): self.root.update_idletasks(); self.root.update(); time.sleep(0.015)
            self._splash_step("connecting to the game…")
            def bgwork():                                                        # heavy work OFF the main thread
                o=False; t0=time.time()
                while time.time()-t0 < 8.0:
                    try: o=self.eng.tick()
                    except Exception: o=False
                    if o: break
                    time.sleep(0.2)
                d={"ok":o}
                if o:
                    try: d["stats"]=self.eng.read_stats()
                    except Exception: d["stats"]=None
                    try: d["stage"]=self.eng.read_stage()
                    except Exception: d["stage"]=None
                    try: d["inv"]=self._read_inv_rows()
                    except Exception: d["inv"]=None
                self._boot=d
            threading.Thread(target=bgwork,daemon=True).start()
            # main thread: just keep the curtain SMOOTH until the background work finishes
            while self._boot is None:
                self.root.update_idletasks(); self.splash.update(); time.sleep(0.016)
            d=self._boot
            if d.get("ok"):                                                      # render loaded data (fast) behind curtain
                self._was_conn=True
                self.conn.configure(text="●  connected   ·   game v%s   ·   build %s   ·   offsets ✓"%(
                C.game_version() or "?", (C.dll_hash() or "?")[:6]),text_color=GRN)
                if d.get("stats"): self._fill_stats(d["stats"])
                if d.get("stage"): self._fill_stage(d["stage"])
                self._splash_step("loading inventory…"); self.splash.update()
                if d.get("inv"): rows,okinv=d["inv"]; self._render_inv(rows,okinv)
            else:
                self.conn.configure(text="●  game closed — reconnects on reopen",text_color=RED)
            for _ in range(3): self.root.update_idletasks(); self.root.update(); time.sleep(0.02)
        finally:
            try: self.splash.destroy()
            except Exception: pass
        self._start_engine_thread()
        self.root.after(3500, self._check_update_async)                     # checa update no GitHub ~3.5s apos abrir (thread, silencioso)

    def _check_update_async(self):
        if not getattr(sys,"frozen",False): return                          # auto-update so faz sentido no .exe congelado
        def work():
            try: info=C.check_update()
            except Exception: info=None
            if info:
                try: self.root.after(0, lambda: self._show_update_banner(info))
                except Exception: pass
        threading.Thread(target=work,daemon=True).start()

    def _show_update_banner(self, info):
        self._upd_info=info
        self.upd_lbl.configure(text="🔄  Atualização disponível: %s  (você tem v%s)"%(info["tag"], C.VERSION))
        self.upd_btn.configure(state="normal", text="Atualizar agora")
        try: self.upd_bar.pack(fill="x", padx=14, pady=(4,2), before=self.tabs)   # logo abaixo do header, acima das abas
        except Exception: self.upd_bar.pack(fill="x", padx=14, pady=(4,2))
        self.log("Atualização %s disponível — clique em 'Atualizar agora' no topo."%info["tag"])

    def _start_update(self):
        info=getattr(self,"_upd_info",None)
        if not info: return
        if not getattr(sys,"frozen",False):
            self.log("auto-update só roda no .exe (aqui é o .py de desenvolvimento)."); return
        self.upd_btn.configure(state="disabled", text="Baixando… 0%")
        def prog(frac):
            try: self.root.after(0, lambda: self.upd_btn.configure(text="Baixando… %d%%"%int(frac*100)))
            except Exception: pass
        def work():
            try:
                newexe,exe,exedir=C.download_update(info["url"], progress=prog)
                C.launch_updater(newexe,exe,exedir)                         # .bat espera este processo sair, troca o exe e reabre
                self.root.after(0, self._finish_update)
            except Exception as e:
                self.root.after(0, lambda: (self.upd_btn.configure(state="normal",text="Tentar de novo"),
                                            self.log("update falhou: %s"%e)))
        threading.Thread(target=work,daemon=True).start()

    def _finish_update(self):
        self.log("baixado ✓ — fechando pra trocar o exe e reabrir sozinho…")
        try:
            if self.overlay_proc and self.overlay_proc.poll() is None: self.overlay_proc.terminate()
        except Exception: pass
        try: self.root.update(); self.root.destroy()
        except Exception: pass
        os._exit(0)                                                         # sai JA pra liberar o exe pro updater trocar

    def log(self,m):
        self._logbuf.append(time.strftime("%H:%M:%S ")+m); self._logbuf=self._logbuf[-200:]
        try: self.status.configure(text=m[:120])
        except Exception: pass

    def _style_tree(self):
        st=ttk.Style(); st.theme_use("default")
        for s in ("inv","mkt"):
            st.configure(s+".Treeview",background=CARD,fieldbackground=CARD,foreground=FG,rowheight=26,
                         font=("Consolas",10),borderwidth=0)
            st.configure(s+".Treeview.Heading",background=SURF,foreground=SUB,font=("Consolas",10,"bold"),
                         relief="flat",borderwidth=0)
            st.map(s+".Treeview.Heading",background=[("active",STROKE)],foreground=[("active",ACC)])
            st.map(s+".Treeview",background=[("selected","#2a1d10")],foreground=[("selected",ACC)])
        st.configure("TScrollbar",background=CARD2,troughcolor=W_BG,bordercolor=W_BG,arrowcolor=SUB)

    # ---- widget helpers ----
    def card(self,parent,title=None,tcolor=ACC):
        c=ctk.CTkFrame(parent,fg_color=CARD,corner_radius=10,border_width=1,border_color=STROKE)
        if title:
            ctk.CTkLabel(c,text=title,text_color=tcolor,font=MONO(12),anchor="w").pack(fill="x",padx=16,pady=(12,2))
        return c
    def btn(self,parent,text,cmd,color=CARD2,hover=STROKE2,tcolor=FG,w=None,fs=12):
        return ctk.CTkButton(parent,text=text,command=cmd,fg_color=color,hover_color=hover,text_color=tcolor,
                             corner_radius=8,font=F(fs,"bold"),width=(w or 0),height=30,border_spacing=6,
                             border_width=1,border_color=STROKE2)
    def abtn(self,parent,text,cmd,w=None):
        return ctk.CTkButton(parent,text=text,command=cmd,fg_color=ACC,hover_color=ACC_H,text_color=ACC_TXT,
                             corner_radius=8,font=F(12,"bold"),width=(w or 0),height=30)

    def _build(self):
        # header
        top=ctk.CTkFrame(self.root,fg_color="transparent"); top.pack(fill="x",padx=18,pady=(14,8))
        brand=ctk.CTkFrame(top,fg_color="transparent"); brand.pack(side="left")
        ctk.CTkLabel(brand,text="tbh_bot",text_color=FG,font=MONO(21)).pack(side="left")
        ctk.CTkLabel(brand,text="◦",text_color=ACC,font=MONO(21)).pack(side="left",padx=(6,0))
        self.conn=ctk.CTkLabel(top,text="● connecting…",text_color=SUB,font=MONO(11)); self.conn.pack(side="right",pady=4)
        # hairline under header
        ctk.CTkFrame(self.root,fg_color=STROKE,height=1,corner_radius=0).pack(fill="x",padx=18,pady=(0,4))
        # --- banner de UPDATE (criado escondido; _show_update_banner o exibe se houver versao nova no GitHub) ---
        self.upd_bar=ctk.CTkFrame(self.root,fg_color=CARD2,corner_radius=8,border_width=1,border_color=ACC)
        self.upd_lbl=ctk.CTkLabel(self.upd_bar,text="",text_color=FG,font=F(12,"bold"),anchor="w")
        self.upd_lbl.pack(side="left",padx=(14,8),pady=8)
        ctk.CTkButton(self.upd_bar,text="✕",width=30,command=lambda:self.upd_bar.pack_forget(),
                      fg_color="transparent",hover_color=STROKE,text_color=SUB,font=F(13)).pack(side="right",padx=(0,6),pady=6)
        self.upd_btn=ctk.CTkButton(self.upd_bar,text="Atualizar agora",width=150,command=self._start_update,
                                   fg_color=ACC,hover_color=ACC_H,text_color=ACC_TXT,font=F(12,"bold"))
        self.upd_btn.pack(side="right",padx=(4,4),pady=6)
        # tabs
        self.tabs=ctk.CTkTabview(self.root,fg_color=W_BG,segmented_button_fg_color=SURF,
                                 segmented_button_selected_color=CARD2,segmented_button_selected_hover_color=STROKE,
                                 segmented_button_unselected_color=SURF,text_color=SUB,corner_radius=8,border_width=0)
        self.tabs.pack(fill="both",expand=True,padx=14,pady=2)
        for t in ("Trainer","Inventory","Market","Runes","Stages"): self.tabs.add(t)
        try: self.tabs._segmented_button.configure(font=MONO(12),text_color=SUB,
                                                    selected_color=CARD2,text_color_disabled=SUBTLE)
        except Exception: pass
        self._splash_step("building Trainer…");   self._build_trainer(self.tabs.tab("Trainer"))
        self._splash_step("building Inventory…"); self._build_inv(self.tabs.tab("Inventory"))
        self._splash_step("building Market…");    self._build_mkt(self.tabs.tab("Market"))
        self._splash_step("building Runes…");     self._build_runes(self.tabs.tab("Runes"))
        self._splash_step("building Stages…");    self._build_stages(self.tabs.tab("Stages"))
        # bottom bar
        bar=ctk.CTkFrame(self.root,fg_color=SURF,corner_radius=0,height=48); bar.pack(fill="x",side="bottom")
        ctk.CTkFrame(bar,fg_color=STROKE,height=1,corner_radius=0).pack(fill="x",side="top")
        self.ov_btn=self.btn(bar,"◉  Price overlay: OFF",self.toggle_overlay)
        self.ov_btn.pack(side="left",padx=(10,4),pady=8)
        self.btn(bar,"▶  Open game",lambda:self.eng.launch_game()).pack(side="left",padx=4,pady=8)
        self.status=ctk.CTkLabel(bar,text="",text_color=SUBTLE,font=MONO(10),anchor="e"); self.status.pack(side="right",fill="x",expand=True,padx=12)

    # ---------- Trainer ----------
    def _build_trainer(self,f):
        wrap=ctk.CTkScrollableFrame(f,fg_color="transparent"); wrap.pack(fill="both",expand=True,padx=2,pady=2)
        # protection
        c1=self.card(wrap," PROTECTION & COMBAT "); c1.pack(fill="x",padx=6,pady=(6,6))
        self.v_actk=tk.BooleanVar(); self.v_god=tk.BooleanVar()
        self.v_autobox=tk.BooleanVar(); self.v_autoitem=tk.BooleanVar(); self.v_autosynth=tk.BooleanVar()
        self.v_watchdog=tk.BooleanVar()
        self.v_autoboss=tk.BooleanVar()
        self.v_evolve=tk.BooleanVar()
        # GRADE 2 colunas x N linhas, em vez de empilhar tudo lado a lado numa linha:
        # com pack(side="left") o 4o switch de cada linha VAZAVA pra fora da janela (o Auto-boss e o
        # Evolution so apareciam esticando o painel) e as descricoes eram cortadas no meio. Com grid +
        # colunas de peso igual + wraplength, cabe sempre e a descricao quebra linha em vez de sumir.
        # Ao adicionar switch novo: so por na lista — a grade se ajusta sozinha (nao ha mais indice
        # manual pra esquecer, que foi o KeyError que deixou o painel sem abrir na v2.6).
        gr=ctk.CTkFrame(c1,fg_color="transparent"); gr.pack(fill="x",padx=16,pady=(2,12))
        gr.grid_columnconfigure((0,1),weight=1,uniform="sw")
        SW=[("ACTk Bypass",self.v_actk,"actk","hides the cheats from the anti-cheat"),
            ("God Mode",self.v_god,"god","player takes no damage"),
            ("🎁 Auto-box",self.v_autobox,"autobox","opens boxes as they appear"),
            ("📦 Auto-stash",self.v_autoitem,"autoitem","sends every item to the stash instantly"),
            ("⚗️ Auto-fuse",self.v_autosynth,"autosynth","opens the cube for you → fuses 9 same-grade at Lv.65~80 (set the type/level once)"),
            ("🛡 Auto-restart",self.v_watchdog,"watchdog","game closed? reopens via Steam in 15s + re-applies everything"),
            ("🗡 Auto-boss",self.v_autoboss,"autoboss","spends a soulstone on the act boss (x-10) and comes back"),
            ("📈 Evolution",self.v_evolve,"evolve","always on your newest stage, 1-1 → Torment 3-9")]
        # Auto-fuse: real, legit synthesis (no forging). SAFE 65-80 mode: the bot only full-fills
        # (ipu, which respects the Cube's Lv dropdown) and fuses (imx) IF the result level >= 65 —
        # it never fuses low-level junk. Set the Cube type + "Lv.65~80" yourself; the bot won't
        # touch the type/level (inf/ilo would reset it to 1-10 and burn low-level items).
        for idx,(txt,var,key,desc) in enumerate(SW):
            # descricao EMBAIXO do switch (nao ao lado): ao lado ela ficava espremida em ~250px e
            # quebrava no meio da palavra. Embaixo ela usa a coluna inteira e continua legivel.
            cell=ctk.CTkFrame(gr,fg_color="transparent")
            cell.grid(row=idx//2,column=idx%2,sticky="ew",padx=(0,16),pady=(3,7))
            ctk.CTkSwitch(cell,text=txt,variable=var,onvalue=True,offvalue=False,
                          command=lambda k=key,v=var:self._set_want(k,v),font=F(13,"bold"),
                          progress_color=ACC,button_color="#e5e5e5",button_hover_color="#fff",
                          fg_color=CARD2,text_color=FG).pack(anchor="w")
            dcolor=AMBER if key=="autosynth" else SUBTLE   # auto-fuse: aviso do cubo aberto em destaque
            ctk.CTkLabel(cell,text=desc,text_color=dcolor,justify="left",wraplength=360,anchor="w",
                         font=F(10,"bold" if key=="autosynth" else "normal")).pack(anchor="w",padx=(46,0))
        # --- Auto-fuse: TETO DE GRADE + TIPOS (sincroniza com engine.want -> _do_synth respeita/nao passa do teto) ---
        self.SYNTH_GRADES=["Common","Uncommon","Rare","Legendary","Immortal","Arcana","Beyond","Celestial","Divine","Cosmic"]
        af=ctk.CTkFrame(c1,fg_color=CARD2,corner_radius=8); af.pack(fill="x",padx=16,pady=(0,12))
        r1=ctk.CTkFrame(af,fg_color="transparent"); r1.pack(fill="x",padx=12,pady=(10,2))
        ctk.CTkLabel(r1,text="⚗️ Auto-fuse — fundir ATÉ o grade:",text_color=FG,font=F(11,"bold")).pack(side="left")
        self.v_synth_grade=tk.StringVar(value="Rare")
        ctk.CTkOptionMenu(r1,variable=self.v_synth_grade,values=self.SYNTH_GRADES,width=140,font=F(11),
                          fg_color=CARD2,button_color=STROKE2,button_hover_color=SUB,
                          dropdown_fg_color=CARD2,dropdown_hover_color=STROKE,
                          command=lambda _v:self._sync_synth_opts()).pack(side="left",padx=8)
        r2=ctk.CTkFrame(af,fg_color="transparent"); r2.pack(fill="x",padx=12,pady=(0,2))
        ctk.CTkLabel(r2,text="tipos:",text_color=SUB,font=F(11)).pack(side="left",padx=(0,4))
        self.v_synth_t=[tk.BooleanVar(value=True),tk.BooleanVar(value=True),tk.BooleanVar(value=True)]
        for i,tn in enumerate(["Equipment","Accessory","Material"]):
            ctk.CTkCheckBox(r2,text=tn,variable=self.v_synth_t[i],onvalue=True,offvalue=False,
                            command=self._sync_synth_opts,font=F(11),text_color=FG,fg_color=ACC,
                            hover_color=ACC_H,checkmark_color=ACC_TXT,border_color=SUBTLE,
                            corner_radius=4,checkbox_width=18,checkbox_height=18).pack(side="left",padx=6)
        ctk.CTkLabel(af,text="funde do grade menor ATÉ o escolhido; nunca acima. Ex.: 'Rare' funde common+uncommon+rare (rare→legendary). 'Uncommon' preserva seus rares.",
                     text_color=SUBTLE,font=F(9),wraplength=430,justify="left").pack(anchor="w",padx=14,pady=(2,6))
        # --- Nivel do CUBO: gateia quais recipes aparecem (cubo baixo NAO ve o tier 65~80). So o runtime conta. ---
        rc=ctk.CTkFrame(af,fg_color="transparent"); rc.pack(fill="x",padx=12,pady=(2,2))
        ctk.CTkLabel(rc,text="🧊 Nível do cubo:",text_color=FG,font=F(11,"bold")).pack(side="left")
        self.cube_lvl_lbl=ctk.CTkLabel(rc,text="—",text_color=AMBER,font=MONO(12)); self.cube_lvl_lbl.pack(side="left",padx=(6,10))
        self.btn(rc,"→ Lv.100 (libera fusão 65+)",self._cube_unlock,color="#7a4a12",hover="#8a5a1a",tcolor="#ffd9a0",fs=11).pack(side="left")
        self.btn(rc,"↻",self._cube_read,w=34,fs=12).pack(side="left",padx=4)
        self.cube_lvl_hint=ctk.CTkLabel(af,text="cubo baixo não enxerga as fusões de nível alto — suba o cubo p/ liberar o tier 65~80.",
                                        text_color=SUBTLE,font=F(9),wraplength=430,justify="left"); self.cube_lvl_hint.pack(anchor="w",padx=14,pady=(0,10))
        self._sync_synth_opts(log=False)   # empurra os defaults (Rare + todos os tipos) pro engine.want
        self._cube_read()
        # stats
        c2=self.card(wrap," STATS  ·  tick, type a value (re-applies itself) "); c2.pack(fill="x",padx=6,pady=6)
        tb=ctk.CTkFrame(c2,fg_color="transparent"); tb.pack(fill="x",padx=14,pady=(2,4))
        self.btn(tb,"Read current",self.read_stats_ui).pack(side="left",padx=(0,4))
        self.btn(tb,"Clear",self.clear_stats,tcolor=RED).pack(side="left",padx=4)
        ctk.CTkLabel(tb,text="  Profile:",text_color=SUB,font=MONO(11)).pack(side="left",padx=(10,2))
        self.prof_cb=ctk.CTkComboBox(tb,width=150,font=MONO(11),fg_color=CARD2,button_color=STROKE2,
                                     button_hover_color=SUB,border_color=STROKE,dropdown_fg_color=CARD2,
                                     dropdown_hover_color=STROKE,values=[]); self.prof_cb.set("")
        self.prof_cb.pack(side="left",padx=3)
        self.btn(tb,"💾",self.save_profile,w=38,fs=13).pack(side="left",padx=2)
        self.btn(tb,"📂",self.load_profile,w=38,fs=13).pack(side="left",padx=2)
        self.btn(tb,"🗑",self.delete_profile,tcolor=RED,w=38,fs=13).pack(side="left",padx=2)
        self.act_lbl=ctk.CTkLabel(c2,text="",text_color=GRN,font=MONO(11),anchor="w")
        self.act_lbl.pack(fill="x",padx=16,pady=(0,4))
        # SEM rolagem propria: a pagina (wrap) ja rola. Antes isto era um CTkScrollableFrame travado
        # em height=290 -> rolagem DENTRO de rolagem: a lista de stats cortava no meio (so ~9 dos 25
        # cabiam) e o STAGE ficava espremido. Agora a lista cresce e existe UMA barra so, a da pagina.
        sf=ctk.CTkFrame(c2,fg_color=CARD2,corner_radius=8)
        sf.pack(fill="both",expand=True,padx=12,pady=(0,12))
        sf.grid_columnconfigure(1,weight=1)
        rr=0
        for gi,(gname,members) in enumerate(STAT_GROUPS):
            ctk.CTkLabel(sf,text=gname,text_color=ACC,font=MONO(11)).grid(row=rr,column=0,columnspan=4,sticky="w",padx=6,pady=(12 if gi else 4,2)); rr+=1
            for name in members:
                cvar=tk.BooleanVar(); evar=tk.StringVar()
                ctk.CTkCheckBox(sf,text=name,variable=cvar,onvalue=True,offvalue=False,command=self.sync_stats,
                                font=F(11),text_color=FG,fg_color=ACC,hover_color=ACC_H,checkmark_color=ACC_TXT,
                                border_color=SUBTLE,corner_radius=4,checkbox_width=18,checkbox_height=18).grid(
                                row=rr,column=0,columnspan=2,sticky="w",padx=(8,0),pady=1)
                e=ctk.CTkEntry(sf,textvariable=evar,width=120,font=MONO(11),fg_color=CARD,border_color=STROKE,corner_radius=6)
                e.grid(row=rr,column=2,padx=6,pady=1,sticky="e")
                e.bind("<Return>",lambda ev:self.sync_stats()); e.bind("<FocusOut>",lambda ev:self.sync_stats())
                cur=ctk.CTkLabel(sf,text="",text_color=SUBTLE,font=MONO(10),width=90,anchor="w"); cur.grid(row=rr,column=3,sticky="w",padx=(2,8))
                self.stat_vars[name]=(cvar,evar,cur); rr+=1
        # stage
        c3=self.card(wrap," STAGE  ·  ⚠ Act/StageNo may be rejected by the server ",tcolor=RED); c3.pack(fill="x",padx=6,pady=(6,10))
        gt=ctk.CTkFrame(c3,fg_color="transparent"); gt.pack(fill="x",padx=16,pady=(2,4))
        self.v_stage=tk.BooleanVar()
        ctk.CTkSwitch(gt,text="Apply stage edits",variable=self.v_stage,onvalue=True,offvalue=False,
                      command=self.sync_stage,font=F(12,"bold"),progress_color=RED,fg_color=CARD2,text_color=FG).pack(side="left")
        self.btn(gt,"Read stage",self.read_stage_ui).pack(side="right")
        grid=ctk.CTkFrame(c3,fg_color="transparent"); grid.pack(fill="x",padx=14,pady=(0,12))
        for i in range(3): grid.grid_columnconfigure(i,weight=1)
        for i,fld in enumerate(STAGE_SHOW):
            r_,c_=divmod(i,3); cell=ctk.CTkFrame(grid,fg_color="transparent"); cell.grid(row=r_,column=c_,sticky="w",padx=3,pady=2)
            cvar=tk.BooleanVar(); evar=tk.StringVar()
            ctk.CTkCheckBox(cell,text=fld,variable=cvar,onvalue=True,offvalue=False,command=self.sync_stage,
                            font=F(10),text_color=FG,fg_color=ACC,hover_color=ACC_H,checkmark_color=ACC_TXT,
                            border_color=SUB,corner_radius=5,checkbox_width=17,checkbox_height=17).pack(side="left")
            e=ctk.CTkEntry(cell,textvariable=evar,width=64,font=MONO(10),fg_color=CARD,border_color=STROKE,corner_radius=6)
            e.pack(side="left",padx=4); e.bind("<Return>",lambda ev:self.sync_stage()); e.bind("<FocusOut>",lambda ev:self.sync_stage())
            self.stage_vars[fld]=(cvar,evar,None)
        self._refresh_prof_combo(); self._upd_active()

    def _set_want(self,key,var):
        self.eng.want[key]=bool(var.get())
        if key in ("autobox","autoitem","autosynth") and var.get() and not self.v_actk.get():   # detectable hook -> turn ACTk on too
            self.v_actk.set(True); self.eng.want["actk"]=True
            self.log("ACTk Bypass turned on too (uses the hook)")
        if key=="autosynth" and var.get():                                  # Auto-fuse = FULL pipeline
            if not self.v_autoitem.get():
                self.v_autoitem.set(True); self.eng.want["autoitem"]=True   # stash: send items to the bank
            if not self.v_autobox.get():
                self.v_autobox.set(True); self.eng.want["autobox"]=True     # box: open boxes -> generate items
            self.eng.want["synth_lvlgate"]=True
            self._sync_synth_opts(log=False)                                # garante teto/tipos no want (caso tenha sido resetado)
            self.log("⚗️ Auto-fuse ON: funde até o grade/tipos escolhidos no Lv.65~80, ciclando sozinho.")
        self._upd_active()
    def _sync_synth_opts(self, log=True):
        """SINCRONIZA a escolha (teto de grade + tipos) do auto-fuse pro engine.want. O _do_synth le
        want[synth_maxgrade] (0=common..) e want[synth_types] ({0=Equip,1=Acess,2=Mat}) e NUNCA funde
        acima do teto nem tipo desmarcado. Chamado a cada mudanca dos controles + no build + no load."""
        try: mg=self.SYNTH_GRADES.index(self.v_synth_grade.get())
        except (ValueError,AttributeError): mg=2
        types=set(i for i in range(3) if self.v_synth_t[i].get()) if getattr(self,"v_synth_t",None) else {0,1,2}
        self.eng.want["synth_maxgrade"]=mg
        self.eng.want["synth_types"]=types
        if log:
            tn=", ".join(["Equip","Acess","Mat"][i] for i in sorted(types)) or "(nenhum → não funde)"
            self.log("⚗️ Auto-fuse: fundir até %s · tipos: %s"%(self.v_synth_grade.get(),tn))
    def _confirm_close(self,what):
        """Confirma uma acao que fecha o jogo (~12s, anti-cheat cliente) mas persiste no reload."""
        from tkinter import messagebox
        return messagebox.askyesno("Desbloqueio — o jogo vai fechar",
            "%s\n\nComo é um valor protegido, o jogo FECHA sozinho em ~12s (anti-cheat do cliente).\n"
            "O valor fica SALVO — é só REABRIR o jogo que já estará desbloqueado.\n\nContinuar?"%what,icon="warning")
    def _cube_read(self):
        if not getattr(self,"cube_lvl_lbl",None): return
        def w():
            lv=self.eng.cube_level() if self.eng else None
            self.root.after(0,lambda:self.cube_lvl_lbl.configure(text=("Lv.%d"%lv) if lv is not None else "—"))
        threading.Thread(target=w,daemon=True).start()
    def _cube_unlock(self):
        if not self.eng: self.cube_lvl_hint.configure(text="jogo fechado"); return
        if not self._confirm_close("Subir o nível do cubo para 100 (libera fundir itens Lv.65+)."): return
        def w():
            ok,lv=self.eng.set_cube_level(100)
            def done():
                if ok:
                    self.cube_lvl_lbl.configure(text="Lv.%d"%lv)
                    self.cube_lvl_hint.configure(text="✔ cubo → Lv.%d. O jogo vai fechar em ~12s — reabra e ABRA O CUBO: as fusões 65~80 estarão liberadas (fica salvo)."%lv)
                else:
                    self.cube_lvl_hint.configure(text="falhou — abra o jogo (e o cubo já foi inicializado?) e tente de novo.")
            self.root.after(0,done)
        threading.Thread(target=w,daemon=True).start()
    def sync_stats(self):
        d={}
        for name,(cvar,evar,cur) in self.stat_vars.items():
            if cvar.get():
                try: d[name]=float(evar.get())
                except Exception: pass
        self.eng.stats=d; self._upd_active()
    def sync_stage(self):
        d={}
        if self.v_stage.get():
            for fld,(cvar,evar,cur) in self.stage_vars.items():
                if cvar.get():
                    try: d[fld]=int(float(evar.get()))
                    except Exception: pass
        self.eng.stage=d; self._upd_active()
    def _upd_active(self):
        prot=[n for n,v in [("ACTk",self.v_actk),("God",self.v_god)] if v.get()]
        parts=[]
        if prot: parts.append("+".join(prot))
        if getattr(self,"v_autobox",None) and self.v_autobox.get(): parts.append("Auto-box🎁")
        if getattr(self,"v_autoitem",None) and self.v_autoitem.get(): parts.append("Auto-stash📦")
        if getattr(self,"v_autosynth",None) and self.v_autosynth.get(): parts.append("Auto-fuse⚗️")
        if self.eng.stats: parts.append("%d stats"%len(self.eng.stats))
        if self.eng.stage: parts.append("%d stage"%len(self.eng.stage))
        self.act_lbl.configure(text=("● active:  "+"   ·   ".join(parts)) if parts else "○ nothing active",
                               text_color=GRN if parts else SUBTLE)
    def read_stats_ui(self):
        threading.Thread(target=lambda:self.root.after(0,lambda st=self.eng.read_stats():self._fill_stats(st)),daemon=True).start()
    def _fill_stats(self,st):
        for name,(cvar,evar,cur) in self.stat_vars.items():
            v=st.get(name); cur.configure(text=("= %.4g"%v) if v is not None else "= —")
            if v is not None and not evar.get(): evar.set("%.4g"%v)
    def clear_stats(self):
        for cvar,evar,cur in self.stat_vars.values(): cvar.set(False)
        self.eng.stats={}; self._upd_active()
    def read_stage_ui(self):
        threading.Thread(target=lambda:self.root.after(0,lambda sg=self.eng.read_stage():self._fill_stage(sg)),daemon=True).start()
    def _fill_stage(self,sg):
        for fld,(cvar,evar,cur) in self.stage_vars.items():
            v=sg.get(fld)
            if v is not None and not evar.get(): evar.set(str(v))

    # ---------- PERFIS ----------
    def _load_profiles(self):
        try: return json.load(open(PROFILES_FILE,encoding="utf-8"))
        except Exception: return {}
    def _save_profiles(self,d):
        try: json.dump(d,open(PROFILES_FILE,"w",encoding="utf-8"),ensure_ascii=False,indent=1)
        except Exception as e: self.log("error saving profiles: %s"%e)
    def _refresh_prof_combo(self):
        self.prof_cb.configure(values=sorted(self._load_profiles().keys()))
    def _capture(self):
        return {"prot":{"actk":self.v_actk.get(),"god":self.v_god.get(),"autobox":self.v_autobox.get(),"autoitem":self.v_autoitem.get(),"autosynth":self.v_autosynth.get()},
                "stats":{n:{"on":c.get(),"val":e.get()} for n,(c,e,cur) in self.stat_vars.items() if c.get() or e.get()},
                "stage_apply":self.v_stage.get(),
                "stage":{f:{"on":c.get(),"val":e.get()} for f,(c,e,cur) in self.stage_vars.items() if c.get() or e.get()}}
    def _apply_state(self,st):
        p=st.get("prot",{})
        self.v_actk.set(p.get("actk",False)); self.v_god.set(p.get("god",False)); self.v_autobox.set(p.get("autobox",False)); self.v_autoitem.set(p.get("autoitem",False)); self.v_autosynth.set(p.get("autosynth",False))
        # .update (nao reatribuir): reatribuir criava um dict NOVO -> perdia autoboss/evolve/watchdog/synth_* E
        # deixava o _auto_loop (que fez W=self.want UMA vez) lendo o dict VELHO (perfil nao parava o loop). Mutar no lugar preserva as chaves e o loop enxerga.
        self.eng.want.update({"actk":self.v_actk.get(),"god":self.v_god.get(),"hitkill":False,"autobox":self.v_autobox.get(),"autoitem":self.v_autoitem.get(),"autosynth":self.v_autosynth.get()})
        self._sync_synth_opts(log=False)   # re-empurra teto/tipos do auto-fuse
        stt=st.get("stats",{})
        for n,(c,e,cur) in self.stat_vars.items():
            s=stt.get(n); c.set(bool(s and s.get("on"))); e.set(s.get("val","") if s else "")
        self.v_stage.set(st.get("stage_apply",False))
        stg=st.get("stage",{})
        for f,(c,e,cur) in self.stage_vars.items():
            s=stg.get(f); c.set(bool(s and s.get("on"))); e.set(s.get("val","") if s else "")
        self.sync_stats(); self.sync_stage()
    def save_profile(self):
        name=self.prof_cb.get().strip()
        if not name: self.log("type a profile name first"); return
        d=self._load_profiles(); d[name]=self._capture(); self._save_profiles(d)
        self._refresh_prof_combo(); self.log("profile '%s' saved ✓"%name)
    def load_profile(self):
        name=self.prof_cb.get().strip(); d=self._load_profiles()
        if name not in d: self.log("profile '%s' does not exist"%name); return
        self._apply_state(d[name]); self.log("profile '%s' loaded ✓"%name)
    def delete_profile(self):
        name=self.prof_cb.get().strip(); d=self._load_profiles()
        if name in d: del d[name]; self._save_profiles(d); self._refresh_prof_combo(); self.prof_cb.set(""); self.log("profile '%s' deleted"%name)

    # ---------- Inventario ----------
    def _build_inv(self,f):
        top=ctk.CTkFrame(f,fg_color="transparent"); top.pack(fill="x",padx=8,pady=(10,4))
        self.abtn(top,"⟳ Refresh",self.refresh_inv).pack(side="right")
        self.inv_tot=ctk.CTkLabel(top,text="",text_color=GRN,font=MONO(14)); self.inv_tot.pack(side="left")
        card=ctk.CTkFrame(f,fg_color=CARD,corner_radius=10,border_width=1,border_color=STROKE); card.pack(fill="both",expand=True,padx=8,pady=(2,8))
        cols=("name","grade","qty","unit","total")
        self.inv_tree=ttk.Treeview(card,columns=cols,show="headings",style="inv.Treeview")
        heads={"name":"Item","grade":"Grade","qty":"Qty","unit":"Unit $","total":"Total $"}
        w={"name":260,"grade":95,"qty":50,"unit":90,"total":90}; anch={"name":"w","grade":"w","qty":"e","unit":"e","total":"e"}
        self._inv_sort=("total",True); self._inv_rows=[]
        for c in cols:
            self.inv_tree.heading(c,text=heads[c],command=lambda cc=c:self._sort_inv(cc)); self.inv_tree.column(c,width=w[c],anchor=anch[c])
        sb=ttk.Scrollbar(card,orient="vertical",command=self.inv_tree.yview); self.inv_tree.configure(yscrollcommand=sb.set)
        self.inv_tree.pack(side="left",fill="both",expand=True,padx=(8,0),pady=8); sb.pack(side="right",fill="y",pady=8,padx=(0,6))
        self.inv_tree.tag_configure("odd",background=CARD); self.inv_tree.tag_configure("even",background=CARD2); self.inv_tree.tag_configure("hi",foreground=GRN)
    def _read_inv_rows(self):
        inv=self.eng.read_inventory(); rows=[]
        if inv:
            for ik,cnt in inv.items():
                base=self.prices.base_of_key(ik); gi=(ik//1000)%10; up,how=self.prices.price(base,gi)
                rows.append({"name":base or ("?key%d"%ik),"grade":C.GRW.get(gi,"?"),"gi":gi,"qty":cnt,"unit":up,"how":how,"total":(up or 0)*cnt})
        return rows, inv is not None
    def refresh_inv(self):
        def w():
            rows,ok=self._read_inv_rows()
            self.root.after(0,lambda:self._render_inv(rows,ok))
        threading.Thread(target=w,daemon=True).start()
    def _sort_inv(self,col):
        c,rev=self._inv_sort; self._inv_sort=(col,not rev if c==col else (col!="name")); self._render_inv(self._inv_rows,True)
    def _render_inv(self,rows,ok):
        self._inv_rows=rows; col,rev=self._inv_sort
        def key(r): return (r["name"] or "").lower() if col=="name" else r["gi"] if col=="grade" else r["qty"] if col=="qty" else (r["unit"] or -1) if col=="unit" else r["total"]
        rows=sorted(rows,key=key,reverse=rev); self.inv_tree.delete(*self.inv_tree.get_children())
        grand=0; n=0
        for i,r in enumerate(rows):
            grand+=r["total"]; n+=r["qty"]
            us=(("~$%.2f" if r["how"]=="aprox" else "$%.2f")%r["unit"]) if r["unit"] else "—"
            ts=("$%.2f"%r["total"]) if r["unit"] else "—"
            tags=["even" if i%2 else "odd"]+(["hi"] if r["unit"] and r["unit"]>=0.05 else [])
            self.inv_tree.insert("","end",values=(r["name"],r["grade"],r["qty"],us,ts),tags=tags)
        self.inv_tot.configure(text=("Total: $%.2f   •   %d items (%d types)"%(grand,n,len(rows))) if ok else "game closed or resolving offsets…")

    # ---------- Mercado ----------
    # ============================= RUNES TAB (arvore + desbloqueio client-side) =============================
    RUNE_CATCOLOR={"chest":"#f0a83a","combat":"#e5564c","gold":"#e8c93a","exp":"#5cbf6a","util":"#4bb0cc"}
    RUNE_CATLABEL={"chest":"Caixa/Drop","combat":"Combate","gold":"Ouro","exp":"EXP","util":"Utilidade"}

    def _rune_cat(self,name):
        n=(name or "").lower()
        if any(w in n for w in ("chest","drop","openall","openone","autoopen","wavecount")): return "chest"
        if any(w in n for w in ("attackdamage","attackspeed","armor","movespeed")): return "combat"
        if "gold" in n: return "gold"
        if "exp" in n: return "exp"
        return "util"

    def _rune_icon(self,name,size,locked):
        k=(name,size,locked)
        if k in self._rune_imgs: return self._rune_imgs[k]
        try:
            import base64,io
            from PIL import Image,ImageTk
            from tbh_rune_assets import ICONS
            b=ICONS.get(name)
            if not b: return None
            img=Image.open(io.BytesIO(base64.b64decode(b))).convert("RGBA")
            img.thumbnail((size,size),Image.LANCZOS)
            if locked:
                a=img.getchannel("A"); img=img.convert("L").point(lambda v:int(v*0.5)).convert("RGBA"); img.putalpha(a)
            ph=ImageTk.PhotoImage(img); self._rune_imgs[k]=ph; return ph
        except Exception:
            return None

    def _runes_layout(self,defs):
        import collections,sys as _s
        ch={k:[c for c in d["next"] if c in defs] for k,d in defs.items()}
        par=collections.defaultdict(list)
        for kk,cs in ch.items():
            for c in cs: par[c].append(kk)
        roots=[kk for kk in sorted(defs) if not par[kk]]
        depth={}; tpar={}; q=collections.deque()
        for r in roots: depth[r]=0; tpar[r]=None; q.append(r)
        while q:
            n=q.popleft()
            for c in ch[n]:
                if c not in depth: depth[c]=depth[n]+1; tpar[c]=n; q.append(c)
        for kk in defs: depth.setdefault(kk,0)
        tch=collections.defaultdict(list)
        for n,p in tpar.items():
            if p is not None: tch[p].append(n)
        yp={}; ctr=[0.0]; _s.setrecursionlimit(20000)
        def asg(n):
            cc=sorted(tch[n])
            if not cc: yp[n]=ctr[0]; ctr[0]+=1
            else:
                for c in cc: asg(c)
                yp[n]=sum(yp[c] for c in cc)/len(cc)
        for r in roots: asg(r); ctr[0]+=1.3
        for kk in defs:
            if kk not in yp: yp[kk]=ctr[0]; ctr[0]+=1
        CW,RH,PAD=104,58,28
        return {kk:(PAD+depth[kk]*CW,PAD+yp[kk]*RH) for kk in defs}

    def _read_runes_data(self):
        defs=self.eng.read_rune_defs()
        levels=self.eng.read_runes() if defs else None
        return defs,(levels or {})

    def refresh_runes(self):
        def w():
            defs,levels=self._read_runes_data()
            self.root.after(0,lambda:self._render_runes(defs,levels))
        threading.Thread(target=w,daemon=True).start()

    def _rune_togglemode(self):
        self._rune_click_max=not self._rune_click_max
        self._rune_modebtn.configure(text="clique = máx" if self._rune_click_max else "clique = +1")

    def _rune_press(self,e):
        self.rune_canvas.scan_mark(e.x,e.y); self._rune_press_xy=(e.x,e.y); self._rune_moved=False
    def _rune_drag(self,e):
        self.rune_canvas.scan_dragto(e.x,e.y,gain=1)
        if self._rune_press_xy and (abs(e.x-self._rune_press_xy[0])>4 or abs(e.y-self._rune_press_xy[1])>4): self._rune_moved=True
    def _rune_release(self,e):
        if self._rune_moved or not self._rune_defs_cache: return
        cx=self.rune_canvas.canvasx(e.x); cy=self.rune_canvas.canvasy(e.y)
        for it in self.rune_canvas.find_overlapping(cx-2,cy-2,cx+2,cy+2):
            for t in self.rune_canvas.gettags(it):
                if t.startswith("rune_"): self._rune_click(int(t[5:])); return

    def _rune_click(self,key):
        d=(self._rune_defs_cache or {}).get(key)
        if not d: return
        cur=self._rune_levels.get(key,0); mx=d["max"]
        tgt=mx if self._rune_click_max else min(cur+1,mx)
        def w():
            self.eng.set_rune(key,tgt); self.root.after(0,self.refresh_runes)
        threading.Thread(target=w,daemon=True).start()

    def _rune_bulk(self,to_max,cat):
        keys=None
        if cat:
            defs=self._rune_defs_cache or {}
            keys=[k for k,d in defs.items() if self._rune_cat(d["name"])==cat]
        def w():
            n=self.eng.unlock_runes(keys=keys,to_max=to_max)
            self.root.after(0,lambda:(self.rune_status.configure(text="%d runas alteradas"%n),self.refresh_runes()))
        threading.Thread(target=w,daemon=True).start()

    def _build_runes(self,f):
        self._rune_imgs={}; self._rune_defs_cache=None; self._rune_levels={}
        self._rune_click_max=True; self._rune_press_xy=None; self._rune_moved=False
        top=ctk.CTkFrame(f,fg_color="transparent"); top.pack(fill="x",padx=8,pady=(10,4))
        self.abtn(top,"⟳ Refresh",self.refresh_runes).pack(side="right")
        self.btn(top,"Desbloquear TUDO (máx)",lambda:self._rune_bulk(True,None),color="#7a4a12",hover="#8a5a1a",tcolor="#ffd9a0").pack(side="left",padx=(0,6))
        self.btn(top,"Tudo nível 1",lambda:self._rune_bulk(False,None),fs=11).pack(side="left",padx=(0,10))
        for cat in ("chest","combat","gold","exp","util"):
            self.btn(top,self.RUNE_CATLABEL[cat],lambda c=cat:self._rune_bulk(True,c),tcolor=self.RUNE_CATCOLOR[cat],fs=11).pack(side="left",padx=2)
        self._rune_modebtn=self.btn(top,"clique = máx",self._rune_togglemode,fs=11); self._rune_modebtn.pack(side="left",padx=(10,6))
        self.rune_status=ctk.CTkLabel(top,text="",text_color=SUB,font=MONO(11)); self.rune_status.pack(side="left",padx=8)
        card=ctk.CTkFrame(f,fg_color=CARD,corner_radius=10,border_width=1,border_color=STROKE); card.pack(fill="both",expand=True,padx=8,pady=(2,8))
        self.rune_canvas=tk.Canvas(card,bg="#191920",highlightthickness=0,bd=0)
        vsb=ttk.Scrollbar(card,orient="vertical",command=self.rune_canvas.yview)
        hsb=ttk.Scrollbar(card,orient="horizontal",command=self.rune_canvas.xview)
        self.rune_canvas.configure(yscrollcommand=vsb.set,xscrollcommand=hsb.set)
        self.rune_canvas.grid(row=0,column=0,sticky="nsew",padx=(8,0),pady=(8,0))
        vsb.grid(row=0,column=1,sticky="ns",pady=(8,0)); hsb.grid(row=1,column=0,sticky="ew",padx=(8,0))
        card.grid_rowconfigure(0,weight=1); card.grid_columnconfigure(0,weight=1)
        self.rune_canvas.bind("<ButtonPress-1>",self._rune_press)
        self.rune_canvas.bind("<B1-Motion>",self._rune_drag)
        self.rune_canvas.bind("<ButtonRelease-1>",self._rune_release)
        self.rune_canvas.bind("<MouseWheel>",lambda e:self.rune_canvas.yview_scroll(int(-e.delta/120),"units"))
        self.rune_canvas.bind("<Shift-MouseWheel>",lambda e:self.rune_canvas.xview_scroll(int(-e.delta/120),"units"))
        self.rune_canvas.create_text(16,16,anchor="nw",fill=SUB,text="carregando… (abra o jogo para as runas aparecerem)",font=("Consolas",13))
        self.refresh_runes()

    def _render_runes(self,defs,levels):
        c=self.rune_canvas; c.delete("all")
        if not defs:
            c.create_text(16,16,anchor="nw",fill=SUB,text="jogo fechado ou resolvendo offsets…",font=("Consolas",13)); return
        self._rune_defs_cache=defs; self._rune_levels=levels
        pos=self._runes_layout(defs)
        for k,d in defs.items():
            if k not in pos: continue
            x1,y1=pos[k]
            for nx in d["next"]:
                if nx in pos:
                    x2,y2=pos[nx]; c.create_line(x1+20,y1+20,x2+20,y2+20,fill="#33333f",width=2)
        unlocked=0
        for k,d in defs.items():
            if k not in pos: continue
            x,y=pos[k]; lv=levels.get(k,0); mx=d["max"]; cat=self._rune_cat(d["name"])
            locked=(lv<=0); maxed=(lv>=mx and lv>0)
            bd=("#34343e" if locked else ("#ffd24a" if maxed else self.RUNE_CATCOLOR[cat]))
            if lv>=1: unlocked+=1
            tag=("rune_%d"%k,)
            c.create_rectangle(x,y,x+40,y+40,outline=bd,width=2,fill="#26262e",tags=tag)
            img=self._rune_icon(d["icon"],34,locked)
            if img: c.create_image(x+20,y+20,image=img,tags=tag)
            c.create_text(x+20,y+50,text="%d/%d"%(lv,mx),fill=("#ffd24a" if maxed else (FG if lv>0 else SUB)),font=("Consolas",8,"bold"),tags=tag)
        c.configure(scrollregion=c.bbox("all"))
        self.rune_status.configure(text="%d/%d desbloqueadas  ·  arraste=mover, clique numa runa = %s"%(unlocked,len(defs),"máx" if self._rune_click_max else "+1"))

    # ================= STAGES (mapa dos 120 estagios) =================
    STAGE_DIFFCOL={0:"#5cbf6a",1:"#4bb0cc",2:"#e8a13a",3:"#e5564c"}   # Normal/Nightmare/Hell/Torment
    @staticmethod
    def _stage_name(k):
        try: return "%s %d-%d"%(C.Engine.DIFFS[k//1000-1],(k%1000)//100,k%100)
        except Exception: return str(k)

    def _read_stages_data(self):
        tbl=self.eng.stage_table() if self.eng else {}
        mx,cur,_=self.eng.stage_progress() if (self.eng and tbl) else (None,None,None)
        return tbl,mx,cur

    def refresh_stages(self):
        def w():
            tbl,mx,cur=self._read_stages_data()
            self.root.after(0,lambda:self._render_stages(tbl,mx,cur))
        threading.Thread(target=w,daemon=True).start()

    def _stage_hover(self,e):
        if not getattr(self,"_stage_tbl",None): return
        cx=self.stage_canvas.canvasx(e.x); cy=self.stage_canvas.canvasy(e.y)
        for it in self.stage_canvas.find_overlapping(cx-2,cy-2,cx+2,cy+2):
            for t in self.stage_canvas.gettags(it):
                if t.startswith("stg_"):
                    k=int(t[4:]); info=self._stage_tbl.get(k,{}); mx=self._stage_max
                    st=("← ATUAL" if k==self._stage_cur else ("liberado" if (mx is not None and k<=mx) else "bloqueado"))
                    self.stage_hint.configure(text="%s  ·  Lv.%s  ·  %s%s"%(
                        self._stage_name(k),info.get("lvl","?"),st,"  ·  ★ boss (custa soulstone)" if k%100==10 else ""))
                    return

    def _stage_unlock(self,value,label):
        if not self.eng: self.stage_status.configure(text="jogo fechado"); return
        if not self._confirm_close("Liberar estágios até %s."%label): return
        def w():
            ok,val=self.eng.set_maxstage(value)
            msg=("✔ liberado até %s (%d)"%(label,val)) if ok else "falhou — jogo fechado?"
            self.root.after(0,lambda:(self.stage_status.configure(
                text=msg+"  ·  o jogo vai fechar em ~12s (anti-cheat); reabra: o progresso já está salvo"),
                self.root.after(500,self.refresh_stages)))
        threading.Thread(target=w,daemon=True).start()

    def _build_stages(self,f):
        self._stage_tbl=None; self._stage_max=0; self._stage_cur=None
        top=ctk.CTkFrame(f,fg_color="transparent"); top.pack(fill="x",padx=8,pady=(10,4))
        self.abtn(top,"⟳ Refresh",self.refresh_stages).pack(side="right")
        self.btn(top,"Desbloquear TUDO",lambda:self._stage_unlock(C.Engine.STAGE_MAX_KEY,"TORMENT 3-10"),
                 color="#7a4a12",hover="#8a5a1a",tcolor="#ffd9a0").pack(side="left",padx=(0,10))
        for d,nm in enumerate(C.Engine.DIFFS):
            key=(d+1)*1000+310                                    # x-3-10 = ultima fase da dificuldade
            self.btn(top,"até "+nm,lambda k=key,n=nm:self._stage_unlock(k,n+" 3-10"),
                     tcolor=self.STAGE_DIFFCOL[d],fs=11).pack(side="left",padx=2)
        self.stage_status=ctk.CTkLabel(top,text="",text_color=SUB,font=MONO(11)); self.stage_status.pack(side="left",padx=8)
        self.stage_hint=ctk.CTkLabel(f,text="passe o mouse sobre uma fase",text_color=SUBTLE,font=MONO(11),anchor="w")
        self.stage_hint.pack(fill="x",padx=14,pady=(0,2))
        card=ctk.CTkFrame(f,fg_color=CARD,corner_radius=10,border_width=1,border_color=STROKE); card.pack(fill="both",expand=True,padx=8,pady=(2,8))
        self.stage_canvas=tk.Canvas(card,bg="#191920",highlightthickness=0,bd=0)
        vsb=ttk.Scrollbar(card,orient="vertical",command=self.stage_canvas.yview)
        self.stage_canvas.configure(yscrollcommand=vsb.set)
        self.stage_canvas.grid(row=0,column=0,sticky="nsew",padx=(8,0),pady=8)
        vsb.grid(row=0,column=1,sticky="ns",pady=8)
        card.grid_rowconfigure(0,weight=1); card.grid_columnconfigure(0,weight=1)
        self.stage_canvas.bind("<Motion>",self._stage_hover)
        self.stage_canvas.bind("<MouseWheel>",lambda e:self.stage_canvas.yview_scroll(int(-e.delta/120),"units"))
        self.stage_canvas.create_text(16,16,anchor="nw",fill=SUB,text="carregando… (abra o jogo para as fases aparecerem)",font=("Consolas",13))
        self.refresh_stages()

    def _render_stages(self,tbl,mx,cur):
        c=self.stage_canvas; c.delete("all")
        if not tbl:
            c.create_text(16,16,anchor="nw",fill=SUB,text="jogo fechado ou resolvendo offsets…",font=("Consolas",13)); return
        self._stage_tbl=tbl; self._stage_max=(mx or 0); self._stage_cur=cur
        PAD=16; LX=104; CW=52; CELL=40; RH=48; HH=34; GAP=18
        y=PAD; unlocked=0
        for d in range(4):
            base=(d+1)*1000
            dcnt=sum(1 for k in tbl if base<=k<base+1000)
            duc =sum(1 for k in tbl if base<=k<base+1000 and mx is not None and k<=mx)
            c.create_text(PAD,y+HH/2,anchor="w",text=C.Engine.DIFFS[d],fill=self.STAGE_DIFFCOL[d],font=("Consolas",15,"bold"))
            c.create_text(LX+9*CW,y+HH/2,anchor="e",text="%d/%d"%(duc,dcnt),fill=SUB,font=("Consolas",11))
            y+=HH
            for a in range(1,4):
                c.create_text(PAD+6,y+CELL/2,anchor="w",text="Ato %d"%a,fill=SUB,font=("Consolas",11))
                for s in range(1,11):
                    key=base+a*100+s
                    if key not in tbl: continue
                    x=LX+(s-1)*CW
                    lv=(mx is not None and key<=mx); isc=(cur is not None and key==cur); boss=(s==10)
                    if lv: unlocked+=1
                    fill=(ACC if isc else ("#2f2110" if lv else "#141519"))
                    out =(ACC_H if isc else (AMBER if (boss and lv) else (ACC if lv else "#2b2e34")))
                    txt =(ACC_TXT if isc else (FG if lv else "#666b74"))
                    tag=("stg_%d"%key,)
                    c.create_rectangle(x,y,x+CELL,y+CELL,fill=fill,outline=out,width=(2 if (isc or boss) else 1),tags=tag)
                    c.create_text(x+CELL/2,y+CELL/2,text=("★" if boss else str(s)),fill=txt,
                                  font=("Consolas",12,"bold" if (boss or isc) else "normal"),tags=tag)
                y+=RH
            y+=GAP
        c.configure(scrollregion=c.bbox("all"))
        self.stage_status.configure(text="%d/%d liberados  ·  atual: %s"%(unlocked,len(tbl),self._stage_name(cur) if cur else "—"))

    def _build_mkt(self,f):
        bar=ctk.CTkFrame(f,fg_color="transparent"); bar.pack(fill="x",padx=8,pady=(10,4))
        self.mkt_q=ctk.CTkEntry(bar,placeholder_text="🔎  search item…",font=F(11),fg_color=CARD2,border_color=STROKE,corner_radius=8,height=32)
        self.mkt_q.pack(side="left",fill="x",expand=True,padx=(0,6)); self.mkt_q.bind("<KeyRelease>",lambda e:self._render_mkt())
        self.mkt_btn=self.abtn(bar,"⟳ Update prices",self.update_mkt); self.mkt_btn.pack(side="right")
        card=ctk.CTkFrame(f,fg_color=CARD,corner_radius=10,border_width=1,border_color=STROKE); card.pack(fill="both",expand=True,padx=8,pady=(2,4))
        cols=("name","price","listings")
        self.mkt_tree=ttk.Treeview(card,columns=cols,show="headings",style="mkt.Treeview")
        heads={"name":"Item","price":"Price US$","listings":"Listed"}; w={"name":370,"price":100,"listings":80}; anch={"name":"w","price":"e","listings":"e"}
        self._mkt_sort=("price",True)
        for c in cols:
            self.mkt_tree.heading(c,text=heads[c],command=lambda cc=c:self._sort_mkt(cc)); self.mkt_tree.column(c,width=w[c],anchor=anch[c])
        sb=ttk.Scrollbar(card,orient="vertical",command=self.mkt_tree.yview); self.mkt_tree.configure(yscrollcommand=sb.set)
        self.mkt_tree.pack(side="left",fill="both",expand=True,padx=(8,0),pady=8); sb.pack(side="right",fill="y",pady=8,padx=(0,6))
        self.mkt_tree.tag_configure("odd",background=CARD); self.mkt_tree.tag_configure("even",background=CARD2); self.mkt_tree.tag_configure("hi",foreground=GRN)
        self.mkt_upd=ctk.CTkLabel(f,text="",text_color=SUB,font=F(10),anchor="w"); self.mkt_upd.pack(fill="x",padx=12,pady=(0,6))
        self.load_mkt()
    def load_mkt(self):
        try:
            d=json.load(open(C.P("market_prices.json"),encoding="utf-8"))
            self._mkt_items=list(d.get("items",{}).values()); self.mkt_upd.configure(text="updated: "+d.get("updated","?"))
        except Exception: self._mkt_items=[]; self.mkt_upd.configure(text="no data")
        self._render_mkt()
    def _sort_mkt(self,col):
        c,rev=self._mkt_sort; self._mkt_sort=(col,not rev if c==col else (col!="name")); self._render_mkt()
    def _render_mkt(self):
        term=self.mkt_q.get().strip().lower()
        rows=[v for v in self._mkt_items if (not term) or term in (v.get("name") or "").lower()]
        col,rev=self._mkt_sort
        rows.sort(key=lambda v:(v.get("name") or "").lower() if col=="name" else (v.get("listings") or 0) if col=="listings" else (v.get("price_usd") or 0),reverse=rev)
        self.mkt_tree.delete(*self.mkt_tree.get_children())
        for i,v in enumerate(rows):
            p=v.get("price_usd"); price=("$%.2f"%p) if p is not None else "—"
            tags=["even" if i%2 else "odd"]+(["hi"] if p and p>=1 else [])
            self.mkt_tree.insert("","end",values=(v.get("name",""),price,v.get("listings") or 0),tags=tags)
    def update_mkt(self):
        self.mkt_btn.configure(text="downloading…",state="disabled")
        def w():
            try:
                import market_db; market_db.OUT=C.P("market_prices.json"); market_db.main()   # in-process (works in the .exe)
            except Exception as e: self.log("price update failed: %s"%e)
            self.root.after(0,self._mkt_done)
        threading.Thread(target=w,daemon=True).start()
    def _mkt_done(self):
        self.mkt_btn.configure(text="⟳ Update prices",state="normal"); self.prices.load(); self.load_mkt()

    # ---------- Overlay ----------
    def toggle_overlay(self):
        if self.overlay_proc and self.overlay_proc.poll() is None:
            try: self.overlay_proc.terminate()
            except Exception: pass
            self.overlay_proc=None; self.ov_btn.configure(text="◉  Price overlay: OFF",fg_color=CARD2,text_color=FG)
        else:
            try:
                self.overlay_proc=subprocess.Popen(self_launcher()+["--overlay"],cwd=HERE)
                self.ov_btn.configure(text="◉  Price overlay: ON (F9)",fg_color=GREENBG,text_color=ACC)
            except Exception as e: self.log("overlay failed: %s"%e)

    # ---------- engine loop ----------
    def _start_engine_thread(self):
        def loop():
            while True:
                try: ok=self.eng.tick()
                except Exception: ok=False
                # FORA do tick: o tick retorna cedo com o jogo fechado, e e ai que o watchdog age
                try: self.eng.apply_watchdog()
                except Exception: pass
                self.root.after(0,lambda o=ok:self._set_conn(o)); time.sleep(1.2)
        threading.Thread(target=loop,daemon=True).start()
    def _set_conn(self,ok):
        if ok:
            self.conn.configure(text="●  connected   ·   game v%s   ·   build %s   ·   offsets ✓"%(
                C.game_version() or "?", (C.dll_hash() or "?")[:6]),text_color=GRN)
            if not getattr(self,"_was_conn",False):      # RECONNECTED (game reopened) -> refresh everything
                self.read_stats_ui(); self.read_stage_ui(); self.refresh_inv()
                if hasattr(self,"rune_canvas"): self.refresh_runes()
                if hasattr(self,"stage_canvas"): self.refresh_stages()
                if hasattr(self,"cube_lvl_lbl"): self._cube_read()
                self.log("game detected — panel updated")
            self._was_conn=True
        else:
            self.conn.configure(text="●  game closed — reconnects on reopen",text_color=RED)
            self._was_conn=False

def _selftest():
    """Headless self-test INSIDE the frozen exe (same binary the user runs). Exercises the REAL
    automation (auto-box + auto-stash + auto-fuse) via the unified loop and reports each one.
    Usage: TBH_Panel.exe --selftest   -> writes tbh_selftest.log and exits."""
    import threading, time as _t, traceback
    out=C.P("tbh_selftest.log")
    logs=[]
    def w(s):
        try: open(out,"a",encoding="utf-8").write(str(s)+"\n")
        except: pass
    open(out,"w",encoding="utf-8").write("")
    w("== SELFTEST on the frozen exe (frozen=%s) =="%getattr(sys,"frozen",False))
    def stash_items(e):
        try:
            bau=e._resolve_bau(); PSD=e.u64(bau+e.sym.get("inv_psd_off",C.INV_PSD_OFF))
            lp=e.u64(PSD+e.sym.get("stash_off",0x90)); arr=e.u64(lp+0x10); sz=e.u32(lp+0x18) or 0
            return sum(1 for i in range(min(sz,600)) if e.u64(arr+0x20+i*8) and e.u64(e.u64(arr+0x20+i*8)+0x18))
        except Exception: return -1
    try:
        e=C.Engine(log=lambda m:(logs.append(str(m)), w("  [eng] "+str(m))))
        ok=e.attach()
        w("attach=%s pid=%s base=%s"%(ok,e.pid,hex(e.base) if e.base else None))
        crit=["upd","llx","iuw","iw","ipu","imx","cube_slot","izb","inv_slots_off","stash_off"]
        w("offsets: "+", ".join("%s=%s"%(k,hex(e.sym[k]) if isinstance(e.sym.get(k),int) else e.sym.get(k)) for k in crit))
        ri=e.read_inventory(); tot=sum(ri.values()) if ri else 0
        w("INVENTORY: %d total items  -> %s"%(tot, "OK" if ri else "FAILED (empty!)"))
        w("iuw (boxes waiting): NORMAL=%s BOSS=%s ACTBOSS=%s"%(e._iuw_count(0),e._iuw_count(1),e._iuw_count(2)))
        sf=e._cube_sf()
        if sf:
            mn,mx=e._synth_result_lvl(sf); TN={0:"Gear",1:"Accessory",2:"Material"}
            w("CUBE: OPEN, type=%s result Lv %d-%d %s"%(TN.get(e.u32(sf+0x254),"?"),mn,mx,"(65-80 OK)" if (mx or 0)>=65 else "(NOT 65-80 -> auto-fuse will not fuse)"))
        else: w("CUBE: closed (open it at Lv.65~80 to test auto-fuse)")
        # --- run the REAL unified automation for ~16s ---
        e.want.update({"actk":True,"autobox":True,"autoitem":True,"autosynth":True,"synth_lvlgate":True})
        st0=stash_items(e); m0=e._master_item_count()
        w("--- running FULL automation (auto-box + auto-stash + auto-fuse) ~16s ---")
        stop=[False]
        def tickloop():
            while not stop[0]:
                try: e.tick()
                except Exception: pass
                _t.sleep(1.2)
        th=threading.Thread(target=tickloop,daemon=True); th.start()
        _t.sleep(16); stop[0]=True
        boxes=len([l for l in logs if 'Box' in l]); fuses=len([l for l in logs if 'Synthesis' in l])
        st1=stash_items(e); m1=e._master_item_count()
        w("")
        w("RESULTS:")
        w("  AUTO-BOX  : %s"%("WORKING — opened %d box(es)"%boxes if boxes else "no boxes dropped during the 16s window"))
        w("  AUTO-STASH: %s"%("WORKING — stash %d -> %d items (moved %d)"%(st0,st1,st1-st0) if st1>st0 else "no items moved (inventory already empty or stash full)"))
        w("  AUTO-FUSE : %s"%("WORKING — fused %d set(s) at 65-80"%fuses if fuses else "nothing fused (cube not at Lv.65~80, or <9 of a grade at 65-80)"))
        w("  master items %d -> %d | auto_thr alive=%s | game alive=%s"%(m0,m1,bool(getattr(e,'_auto_thr',None) and e._auto_thr.is_alive()),e._proc_alive()))
        w("== VERDICT: attach=%s offsets/inv=%s dispatcher=%s automation=%s =="%(
            ok, "OK" if ri else "X", bool(e.abx), bool(getattr(e,'_auto_thr',None) and e._auto_thr.is_alive())))
    except Exception as ex:
        w("SELFTEST CRASH: "+traceback.format_exc())
    w("== END ==")

def _run():
    if "--selftest" in sys.argv:         # diagnostico headless dentro do exe congelado
        _selftest(); return
    if "--overlay" in sys.argv:          # mesmo .exe roda o overlay quando chamado com --overlay
        import tbh_overlay; tbh_overlay.Overlay().root.mainloop()
    else:
        root=ctk.CTk(); Panel(root); root.mainloop()

if __name__=="__main__":
    try:
        _run()
    except Exception:
        import traceback
        try: open(C.P("tbh_error.log"),"a",encoding="utf-8").write(time.strftime("%Y-%m-%d %H:%M:%S\n")+traceback.format_exc()+"\n")
        except Exception: pass
        raise
