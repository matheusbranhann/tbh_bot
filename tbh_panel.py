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
        root.geometry("880x920"); root.minsize(780,700); root.attributes("-topmost",True)
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
        self._geo=(880,920)
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
                self.conn.configure(text="●  connected   ·   build %s   ·   offsets ✓"%(C.dll_hash() or "?"),text_color=GRN)
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
        # tabs
        self.tabs=ctk.CTkTabview(self.root,fg_color=W_BG,segmented_button_fg_color=SURF,
                                 segmented_button_selected_color=CARD2,segmented_button_selected_hover_color=STROKE,
                                 segmented_button_unselected_color=SURF,text_color=SUB,corner_radius=8,border_width=0)
        self.tabs.pack(fill="both",expand=True,padx=14,pady=2)
        for t in ("Trainer","Inventory","Market"): self.tabs.add(t)
        try: self.tabs._segmented_button.configure(font=MONO(12),text_color=SUB,
                                                    selected_color=CARD2,text_color_disabled=SUBTLE)
        except Exception: pass
        self._splash_step("building Trainer…");   self._build_trainer(self.tabs.tab("Trainer"))
        self._splash_step("building Inventory…"); self._build_inv(self.tabs.tab("Inventory"))
        self._splash_step("building Market…");    self._build_mkt(self.tabs.tab("Market"))
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
        r=ctk.CTkFrame(c1,fg_color="transparent"); r.pack(fill="x",padx=16,pady=(2,2))
        r2=ctk.CTkFrame(c1,fg_color="transparent"); r2.pack(fill="x",padx=16,pady=(2,12))
        rows={0:r,1:r,2:r2,3:r2,4:r2}
        for idx,(txt,var,key,desc) in enumerate([
                ("ACTk Bypass",self.v_actk,"actk","hide the cheats from the anti-cheat"),
                ("God Mode",self.v_god,"god","player takes no damage"),
                ("🎁 Auto-box",self.v_autobox,"autobox","opens boxes as they appear"),
                ("📦 Auto-stash",self.v_autoitem,"autoitem","sends every item to the stash instantly"),
                ("⚗️ Auto-fuse",self.v_autosynth,"autosynth","⚠ ONLY works with the CUBE OPEN at Lv.65~80 → fuses 9 same-grade (result Lv.65+)")]):
                # Auto-fuse: real, legit synthesis (no forging). SAFE 65-80 mode: the bot only full-fills
                # (ipu, which respects the Cube's Lv dropdown) and fuses (imx) IF the result level >= 65 —
                # it never fuses low-level junk. Set the Cube type + "Lv.65~80" yourself; the bot won't
                # touch the type/level (inf/ilo would reset it to 1-10 and burn low-level items).
            cell=ctk.CTkFrame(rows[idx],fg_color="transparent"); cell.pack(side="left",padx=(0,24))
            ctk.CTkSwitch(cell,text=txt,variable=var,onvalue=True,offvalue=False,
                          command=lambda k=key,v=var:self._set_want(k,v),font=F(13,"bold"),
                          progress_color=ACC,button_color="#e5e5e5",button_hover_color="#fff",
                          fg_color=CARD2,text_color=FG).pack(side="left")
            dcolor=AMBER if key=="autosynth" else SUBTLE   # auto-fuse: aviso do cubo aberto em destaque
            ctk.CTkLabel(cell,text=desc,text_color=dcolor,font=F(10,"bold" if key=="autosynth" else "normal")).pack(side="left",padx=(6,0))
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
        sf=ctk.CTkScrollableFrame(c2,fg_color=CARD2,corner_radius=8,height=290)
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
            self.log("⚠ Auto-fuse needs the CUBE OPEN at Lv.65~80 (it won't fuse with the cube closed). Switch the type (Gear/Material/Accessory) yourself.")
        self._upd_active()
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
        self.eng.want={"actk":self.v_actk.get(),"god":self.v_god.get(),"hitkill":False,"autobox":self.v_autobox.get(),"autoitem":self.v_autoitem.get(),"autosynth":self.v_autosynth.get()}
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
                self.root.after(0,lambda o=ok:self._set_conn(o)); time.sleep(1.2)
        threading.Thread(target=loop,daemon=True).start()
    def _set_conn(self,ok):
        if ok:
            self.conn.configure(text="●  connected   ·   build %s   ·   offsets ✓"%(C.dll_hash() or "?"),text_color=GRN)
            if not getattr(self,"_was_conn",False):      # RECONNECTED (game reopened) -> refresh everything
                self.read_stats_ui(); self.read_stage_ui(); self.refresh_inv(); self.log("game detected — panel updated")
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
