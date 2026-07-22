# Feed de offsets

Um `offsets_<hash>.json` por build do jogo. **É daqui que o painel se cura sozinho quando o jogo atualiza.**

`<hash>` = MD5 dos primeiros **2.000.000 bytes** do `GameAssembly.dll`, 12 primeiros hex.

## Por que existe

Quando o jogo atualiza, todos os RVAs mudam e as features que dependem deles param (inventário, runas,
stages, nível do cubo, ACTk). Reextrair exige o Il2CppDumper, que precisa de .NET 6 — quase ninguém tem.

Com o feed, o painel instalado faz um GET de ~13 KB no start, acha o JSON do build novo e volta a
funcionar **na mesma sessão** — sem baixar exe, sem reinstalar, sem ação do usuário. Se o build ainda
não estiver publicado aqui (404), o painel segue em modo degradado (só cheats por AOB) e o banner explica.

Consumido por `src/TbhBot.Core/Update/OffsetsFeed.cs`; o painel grava o resultado em
`<pasta do exe>/cache/offsets_<hash>.json`, que é o primeiro lugar que o `Engine.Attach` procura.

## Como publicar um build novo

Com o jogo atualizado instalado (precisa do Il2CppDumper + .NET 6 — só de quem publica, não do usuário):

```bash
cd python_old_project
py -3 -c "import tbh_core as C; print(C._offsets_ok(C.resolve_symbols(print)))"
# re-dumpa (~40s), extrai e grava cache/offsets_<hash>.json — imprime True se veio completo
```

Depois: copie o JSON pra cá **e** pra `src/TbhBot.Core/Offsets/` (assim o próximo release já sai com ele
embutido, e quem instalar do zero não depende de rede), commit e push. Pronto — os painéis já instalados
se atualizam sozinhos.

As âncoras da extração não dependem de nome ofuscado (só de nomes que o ofuscador preserva: `CubeInData`,
`ERecipeType`, `button_Cube`, `UI_Main`, `Guiding*`…), então um update que só renomeia classes é absorvido
sem tocar em código.

## Validação

```bash
dotnet run --project tools/TbhBot.Cli -- --feed <hash>      # baixa e valida
dotnet run --project tools/TbhBot.Cli -- --e2e             # 19 checagens ao vivo
```

O painel só aceita o JSON se ele carregar e tiver `gra` + `uo_ti` — download pela metade ou página de
erro salva viraria um cache tóxico que nunca mais seria refeito.

## Builds publicados

| hash | quando | nota |
|---|---|---|
| `c824ed7a2bb1` | 2026-07-18 | build da v3.5/v4.0 |
| `535f977c07ca` | 2026-07-22 | update que renomeou a base dos singletons (`nq`→`nr`) e a classe do cubo (`uu.Cube`→`ux.Cube`) |
