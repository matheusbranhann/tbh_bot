#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Baixa TODOS os precos do Steam Market do TBH (appid 3678970) -> market_prices.json.
BLINDADO: nunca sobrescreve os dados bons com vazio/ruim (rate-limit da Steam). So stdlib."""
import json, time, gzip, urllib.request, urllib.parse, urllib.error, os
APPID="3678970"
OUT=os.path.join(os.path.dirname(os.path.abspath(__file__)),"market_prices.json")
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36"

def _existing():
    try: return json.load(open(OUT,encoding="utf-8")).get("items",{}) or {}
    except Exception: return {}

def fetch(start):
    url="https://steamcommunity.com/market/search/render/?"+urllib.parse.urlencode(
        {"appid":APPID,"norender":1,"count":100,"start":start,"currency":1})
    delay=5
    for t in range(5):
        try:
            req=urllib.request.Request(url,headers={"User-Agent":UA})
            with urllib.request.urlopen(req,timeout=25) as r:
                data=r.read()
                if (r.headers.get("Content-Encoding") or "").lower()=="gzip": data=gzip.decompress(data)
                return json.loads(data.decode("utf-8","ignore"))
        except urllib.error.HTTPError as e:
            if e.code==429:                      # rate-limit da Steam -> espera crescente
                print(f"  429 rate-limited, esperando {delay}s..."); time.sleep(delay); delay=min(delay*2,90)
            else:
                print(f"  page {start} HTTP {e.code}"); time.sleep(5)
        except Exception as e:
            print(f"  page {start}: {str(e)[:60]}"); time.sleep(5)
    return None

def main():
    prev=_existing()
    items={}; start=0; total=None; complete=False
    while True:
        d=fetch(start)
        if not d or not d.get("success"): print(f"parou em {start} (incompleto)"); break
        total=d.get("total_count"); res=d.get("results") or []
        if not res: complete=True; break
        for it in res:
            h=it.get("hash_name") or it.get("name"); sp=it.get("sell_price")
            items[h]={"name":it.get("name"),"hash_name":h,
                      "price_usd":(sp/100.0) if sp is not None else None,
                      "price_text":it.get("sell_price_text"),"listings":it.get("sell_listings")}
        start+=len(res); print(f"  {start}/{total} itens")
        if total and start>=total: complete=True; break
        time.sleep(2.0)
    # SEGURANCA: nunca destruir dados bons com um download vazio/parcial (rate-limit)
    if len(items)<50:
        print(f"!! so {len(items)} itens (rate-limit?) — MANTENDO os {len(prev)} existentes, nao sobrescreve."); return
    if prev and len(items)<len(prev)*0.6:
        # pegou bem menos que antes -> mescla (mantem os que sumiram) em vez de perder
        merged=dict(prev); merged.update(items); items=merged
        print(f"pegou menos que antes; mesclado -> {len(items)} itens (nao perde os antigos)")
    # escrita ATOMICA (temp + replace) — nunca deixa o arquivo pela metade
    tmp=OUT+".tmp"
    json.dump({"updated":time.strftime("%Y-%m-%d %H:%M:%S"),"appid":APPID,"count":len(items),"items":items},
              open(tmp,"w",encoding="utf-8"),ensure_ascii=False,indent=1)
    os.replace(tmp,OUT)
    print(f"SALVO {len(items)} itens -> {OUT}")

if __name__=="__main__": main()
