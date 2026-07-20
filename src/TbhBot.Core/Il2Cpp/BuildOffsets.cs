namespace TbhBot.Core.Il2Cpp;

// RVAs conhecidos por build (fallback rapido; se o hash nao bater, re-dumpa sozinho).
// Portado de KNOWN_BUILDS no tbh_core.py: gra, bau_ti (null = resolve em runtime pelo inv_class),
// ynj[], inv_class, e o resto dos offsets vai em Extra (chaves snake_case iguais ao Python).
public sealed record BuildOffsets
{
    public long Gra;
    public long? BauTi;
    public long[] Ynj = [];
    public string InvClass = "";
    public Dictionary<string, long> Extra = new();   // inv_psd_off, inv_list_off, upd, llx, iw, ...
}
