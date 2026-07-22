"""Desbloqueios one-shot chamados pelo dashboard: 'stages' (as 120 fases), 'runes' (todas no max)
e 'cube' (nivel 100 do cubo, que e o que libera a sintese dos tiers 65~80).
Os tres sao client-side. Cuidado embutido: runa NUNCA passa do teto dela (bgpe) -- passar do teto
gera NRE em RuneNode e o jogo fica em loading infinito."""
import sys
HERE = r"C:\tbh_auto"
sys.path.insert(0, HERE)
import tbh_core as C

TARGET = 4310                       # TORMENT 3-10, a maior StageKey -> libera as 120

what = (sys.argv[1] if len(sys.argv) > 1 else "").lower()
if what not in ("stages", "runes", "cube"):
    print("uso: tbh_unlock.py [stages|runes|cube]")
    raise SystemExit(1)

e = C.Engine(log=lambda m: print("[eng]", m, flush=True))
# Engine() so CONSTROI -- quem abre o processo e resolve os simbolos e o attach().
if not e.attach():
    print("ERRO: nao consegui attachar no jogo (ele esta aberto nesta box?)")
    raise SystemExit(2)

if what == "stages":
    cur_max = e.stage_progress()[0]
    print("maxCompletedStage atual: %s" % (cur_max,))
    if cur_max is not None and cur_max >= TARGET:
        # Escrever o ObscuredInt derruba o jogo por ~12s; nao vale a pena se ja esta liberado.
        print("ja esta em %d (120/120 liberadas) - nada a fazer" % cur_max)
        raise SystemExit(0)
    ok, val = e.set_maxstage(TARGET)
    print("maxCompletedStage -> %d (120/120 liberadas) | ok=%s" % (val, ok))
    print("NOTA: escrever esse valor forca o jogo a fechar em ~12s, MAS persiste;")
    print("      o watchdog reabre e o progresso volta ja liberado.")

elif what == "cube":
    # O nivel do cubo indexa a lista de recipes (bam.mey): cubo baixo NAO enxerga o tier 65~80.
    alvo = e.CUBE_MAX_LEVEL
    lv = e.cube_level()
    print("nivel do cubo atual: %s (max %d)" % (lv, alvo))
    if lv is None:
        print("ERRO: o cubo ainda nao resolveu na memoria - espere o jogo carregar e tente de novo")
        raise SystemExit(3)
    if lv >= alvo:
        # Mesmo caso do stages: escrever o ObscuredInt derruba o jogo por ~12s. Nao vale se ja esta no teto.
        print("ja esta no nivel %d - nada a fazer" % lv)
        raise SystemExit(0)
    ok, val = e.set_cube_level(alvo)
    print("nivel do cubo -> %d | ok=%s" % (val, ok))
    print("NOTA: escrever esse valor forca o jogo a fechar em ~12s, MAS persiste;")
    print("      o watchdog reabre e o cubo volta ja no nivel novo (libera o tier 65~80).")
else:
    def censo():
        """(no_maximo, abaixo, total) lendo a lista de runas do save. None se nao deu pra ler --
        assim o '0 runas maxadas' nunca fica ambiguo entre 'ja estava tudo' e 'nao consegui ler'."""
        defs = e.read_rune_defs()
        psd, ro = e._player_psd()
        if not psd:
            return None
        lst = e.u64(psd + ro)
        arr = e.u64(lst + 0x10) if lst else None
        size = e.u32(lst + 0x18) if lst else None
        if not arr or not size:
            return None
        cheias = 0
        for i in range(size):
            r = e.u64(arr + 0x20 + i * 8)
            if not r:
                continue
            mx = int((defs or {}).get(e.u32(r + 0x10), {}).get("max", 1) or 1)
            if e.u32(r + 0x14) >= mx:
                cheias += 1
        return cheias, size - cheias, size

    antes = censo()
    if antes is None:
        print("ERRO: o save do jogador ainda nao carregou - espere o jogo entrar e tente de novo")
        raise SystemExit(3)
    if antes[1] == 0:
        print("as %d runas ja estao no maximo - nada a fazer" % antes[2])
        raise SystemExit(0)

    n = e.unlock_runes(to_max=True)     # respeita o teto de cada runa
    depois = censo() or (0, 0, 0)
    print("runas alteradas: %d | agora %d/%d no maximo" % (n, depois[0], depois[2]))
