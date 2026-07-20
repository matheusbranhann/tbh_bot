#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TBH PRICE OVERLAY (OCR, sem calibracao).
- Passe o mouse sobre um item -> o jogo abre o TOOLTIP (nome + grade).
- O overlay le o tooltip por OCR nativo do Windows, acha o preco REAL do Steam Market
  (market_prices.json, verdade do Steam) e desenha o valor NA PROPRIA TABELA do tooltip.
- Ignora o flag "Untradable" do jogo (muitos vendem na Steam mesmo assim).
- So dispara com o mouse PARADO sobre a janela do jogo (igual o tooltip do jogo).
F9 = fechar.
Deps: winsdk (Windows OCR, offline), numpy, pillow. Dados: market_prices.json (mesma pasta).
"""
import os, json, re, ctypes, asyncio, difflib, collections
import numpy as np
from PIL import ImageGrab
import tkinter as tk
from ctypes import wintypes
from winsdk.windows.media.ocr import OcrEngine
from winsdk.windows.graphics.imaging import SoftwareBitmap, BitmapPixelFormat
from winsdk.windows.storage.streams import DataWriter

import sys
HERE=os.path.dirname(sys.executable) if getattr(sys,"frozen",False) else os.path.dirname(os.path.abspath(__file__))
def P(*a): return os.path.join(HERE,*a)
u32=ctypes.windll.user32
class POINT(ctypes.Structure): _fields_=[("x",ctypes.c_long),("y",ctypes.c_long)]
def cursor():
    p=POINT(); u32.GetCursorPos(ctypes.byref(p)); return p.x,p.y

# ---------- OCR nativo do Windows ----------
ENG=OcrEngine.try_create_from_user_profile_languages()
_LOOP=asyncio.new_event_loop()
def ocr(pil):
    pil=pil.convert("RGBA"); a=np.asarray(pil,dtype=np.uint8)
    h,w=a.shape[:2]; bgra=a[:,:,[2,1,0,3]].tobytes()
    dw=DataWriter(); dw.write_bytes(bgra); buf=dw.detach_buffer()
    sb=SoftwareBitmap.create_copy_from_buffer(buf,BitmapPixelFormat.BGRA8,w,h)
    return _LOOP.run_until_complete(ENG.recognize_async(sb))

def line_box(ln):
    xs=[wd.bounding_rect.x for wd in ln.words]; ys=[wd.bounding_rect.y for wd in ln.words]
    xe=[wd.bounding_rect.x+wd.bounding_rect.width for wd in ln.words]
    ye=[wd.bounding_rect.y+wd.bounding_rect.height for wd in ln.words]
    return min(xs),min(ys),max(xe),max(ye)

# ---------- indice de precos: DIRETO do Steam Market (verdade) ----------
GW={"common":0,"normal":0,"uncommon":1,"rare":2,"legendary":3,"immortal":4,
    "arcana":5,"beyond":6,"celestial":7,"divine":8,"cosmic":9}
GRE=re.compile(r'\b(Common|Normal|Uncommon|Rare|Legendary|Immortal|Arcana|Beyond|Celestial|Divine|Cosmic)\s+Grade\b',re.I)
MKT=re.compile(r'^(.*?)\s*\((\w+)\)\s*([A-Za-z])?\s*$')   # "Base (Grade) A"
def norm(s): return re.sub(r'[^a-z0-9 ]','',(s or '').lower()).strip()

def load_index():
    mk=json.load(open(P("market_prices.json"),encoding="utf-8"))["items"]
    byg=collections.defaultdict(dict)   # base_norm -> {grade_idx: price}  (-1 = material sem grade)
    graded=set()                        # bases que tem grade (gear)
    for n,v in mk.items():
        p=v.get("price_usd")
        if p is None: continue
        m=MKT.match(n)
        if m:
            b=norm(m.group(1)); gi=GW.get(m.group(2).lower())
            if gi is None: gi=-1
            else: graded.add(b)
        else:
            b=norm(n); gi=-1
        d=byg[b]; d[gi]=max(d.get(gi,0.0),p)
    return byg,graded,list(byg.keys())

class Overlay:
    def __init__(self):
        self.byg,self.graded,self.names=load_index()
        self._win()
        self.labels=[]; self._n=0; self._prevkey=None
        self._lx=-999; self._ly=-999          # ultima pos do cursor (detecta parado)
        self._last=None                       # (mx,my,label,frame) ultimo positivo (anti-piscar)
        self.tick()

    def _win(self):
        sw=u32.GetSystemMetrics(0); sh=u32.GetSystemMetrics(1)
        self.root=tk.Tk(); self.root.overrideredirect(True)
        self.root.geometry(f"{sw}x{sh}+0+0"); self.root.attributes("-topmost",True)
        self.root.config(bg="#010101"); self.root.attributes("-transparentcolor","#010101")
        self.cv=tk.Canvas(self.root,width=sw,height=sh,bg="#010101",highlightthickness=0); self.cv.pack()
        self.root.update()
        GWL=-20; LAY=0x80000; TR=0x20; TOOL=0x80; NOACT=0x8000000
        u32.GetWindowLongPtrW.restype=ctypes.c_longlong; u32.GetWindowLongPtrW.argtypes=[ctypes.c_void_p,ctypes.c_int]
        u32.SetWindowLongPtrW.restype=ctypes.c_longlong; u32.SetWindowLongPtrW.argtypes=[ctypes.c_void_p,ctypes.c_int,ctypes.c_longlong]
        u32.GetAncestor.restype=ctypes.c_void_p; u32.GetAncestor.argtypes=[ctypes.c_void_p,ctypes.c_uint]
        u32.WindowFromPoint.restype=ctypes.c_void_p; u32.WindowFromPoint.argtypes=[POINT]
        h=u32.GetAncestor(ctypes.c_void_p(self.root.winfo_id()),2)
        ex=u32.GetWindowLongPtrW(h,GWL); u32.SetWindowLongPtrW(h,GWL,ex|LAY|TR|TOOL|NOACT)
        u32.SetLayeredWindowAttributes.argtypes=[ctypes.c_void_p,wintypes.DWORD,ctypes.c_ubyte,ctypes.c_uint]
        u32.SetLayeredWindowAttributes(h,0x010101,0,0x1)
        self._hwnd=h
        u32.SetWindowPos.argtypes=[ctypes.c_void_p,ctypes.c_ssize_t,ctypes.c_int,ctypes.c_int,ctypes.c_int,ctypes.c_int,ctypes.c_uint]

    def _topmost(self):
        try: u32.SetWindowPos(self._hwnd,-1,0,0,0,0,0x13)
        except Exception: pass

    def resolve_base(self, texts):
        """Dentre varias linhas candidatas, acha a que melhor casa com um item conhecido."""
        base=None; bestr=0.0
        for t in texts:
            nb=norm(t)
            if not nb: continue
            if nb in self.byg: return nb            # match exato ganha na hora
            c=difflib.get_close_matches(nb,self.names,1,0.60)
            if c:
                r=difflib.SequenceMatcher(None,nb,c[0]).ratio()
                if r>bestr and r>=0.70: bestr=r; base=c[0]
        return base

    def price_of(self, base, gradeword):
        gmap=self.byg[base]; gi=GW.get((gradeword or "").lower())
        if base not in self.graded:                     # material -> preco unico exato
            return max(gmap.values()),"ok"
        if gi is not None and gi in gmap: return gmap[gi],"ok"
        if gi is not None:                              # grade nao listado -> grade MAIS PROXIMO
            k=min(gmap,key=lambda kk:abs(kk-gi)); return gmap[k],"aprox"
        return max(gmap.values()),"aprox"

    def _read(self, res, rx0, ry0, SC, mx, my):
        """Retorna o badge (tuple) se achou um tooltip, senao None."""
        cmx,cmy=(mx-rx0)*SC,(my-ry0)*SC
        best=None; bestd=1e18
        for ln in res.lines:
            m=GRE.search(ln.text)
            if not m: continue
            x0,y0,x1,y1=line_box(ln)
            dc=((x0+x1)/2-cmx)**2+((y0+y1)/2-cmy)**2
            if dc<bestd: bestd=dc; best=(m.group(1),(x0,y0,x1,y1))
        if not best: return None
        gword,(gx0,gy0,gx1,gy1)=best
        bx=int(rx0+gx1/SC+14); by=int(ry0+(gy0+gy1)/2/SC)
        # "Untradable" no jogo = NAO vende na Steam (confirmado) -> nao mostra preco enganoso
        if any('untradable' in l.text.lower() for l in res.lines):
            return ("badge",bx,by,"Untradable","#3a2323","#d08a8a")
        # NOME = melhor item conhecido entre TODAS as linhas acima da linha de grade
        above=[]
        for ln in res.lines:
            lx0,ly0,lx1,ly1=line_box(ln)
            dy=gy0-ly1
            if ly1<=gy0+8 and -6<dy<200: above.append((dy,ln.text))
        above.sort()
        base=self.resolve_base([t for _,t in above])
        if base is None: return None
        price,how=self.price_of(base,gword)
        if price is None:
            return ("badge",bx,by,"—","#242424","#7a7a7a")
        txt=("~$ " if how=="aprox" else "$ ")+(f"{price:.2f}" if price<100 else f"{price:.0f}")
        face=("#146c2e" if price>=1 else "#3d5a1e"); ink="#eaffea"
        return ("badge",bx,by,txt,face,ink)

    def scan(self):
        mx,my=cursor()
        gh,r=game_win()
        if not r: self.labels=[]; return
        try: under=int(u32.GetAncestor(u32.WindowFromPoint(POINT(mx,my)),2) or 0)
        except Exception: under=0
        if under!=gh: self.labels=[]; return
        moved=abs(mx-self._lx)>22 or abs(my-self._ly)>22
        self._lx,self._ly=mx,my
        if moved: self.labels=[]; return            # movendo -> nada (mantem _last p/ quando parar)
        rx0=max(r[0],mx-460); ry0=max(r[1],my-460); rx1=min(r[2],mx+460); ry1=min(r[3],my+460)
        if rx1-rx0<80 or ry1-ry0<80: self.labels=[]; return
        try: shot=ImageGrab.grab(bbox=(rx0,ry0,rx1,ry1),all_screens=True)
        except Exception: return
        SC=1.7
        big=shot.resize((int(shot.size[0]*SC),int(shot.size[1]*SC)))
        try: res=ocr(big)
        except Exception: return
        badge=self._read(res,rx0,ry0,SC,mx,my)
        if badge is not None:
            self.labels=[badge]; self._last=(mx,my,badge,self._n)
        else:
            # tooltip ainda nao apareceu / OCR falhou este frame: segura o ultimo (anti-piscar)
            if self._last and abs(mx-self._last[0])<=18 and abs(my-self._last[1])<=18 and self._n-self._last[3]<=6:
                self.labels=[self._last[2]]
            else:
                self.labels=[]

    def draw(self):
        self.cv.delete("all")
        for it in self.labels:
            if it[0]=="badge":
                _,cx,cy,txt,face,ink=it
                t=self.cv.create_text(cx+7,cy,text=txt,fill=ink,anchor="w",font=("Segoe UI",12,"bold"))
                bb=self.cv.bbox(t)
                self.cv.create_rectangle(bb[0]-7,bb[1]-4,bb[2]+7,bb[3]+4,fill=face,outline="#000",width=1)
                self.cv.tag_raise(t)

    def tick(self):
        if u32.GetAsyncKeyState(0x78)&0x8000:   # F9
            try: self.root.destroy()
            except Exception: pass
            return
        self._n+=1
        if self._n%3==0: self._topmost()
        try:
            self.scan()
            key=tuple((it[0],it[1]//6,it[2]//6,it[3]) for it in self.labels)
            if key!=self._prevkey:
                self.draw(); self._prevkey=key
        except Exception: pass
        self.root.after(220,self.tick)

def game_win():
    found=[]
    EnumProc=ctypes.WINFUNCTYPE(ctypes.c_bool,ctypes.c_void_p,ctypes.c_void_p)
    def cb(h,l):
        if not u32.IsWindowVisible(h): return True
        n=u32.GetWindowTextLengthW(h)
        if n:
            b=ctypes.create_unicode_buffer(n+1); u32.GetWindowTextW(h,b,n+1)
            if "TaskBarHero" in b.value or b.value=="TBH":
                rr=wintypes.RECT(); u32.GetWindowRect(h,ctypes.byref(rr))
                found.append((int(h) if h else 0,(rr.left,rr.top,rr.right,rr.bottom)))
        return True
    u32.EnumWindows(EnumProc(cb),0)
    return found[0] if found else (None,None)

if __name__=="__main__":
    Overlay().root.mainloop()
