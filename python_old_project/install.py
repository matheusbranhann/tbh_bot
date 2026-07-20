#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# ============================================================================
#  tbh_bot - instalador (roda sob o Python escolhido pelo INSTALL.bat)
#
#  Faz TODO o trabalho pesado onde e confiavel (Python), nao em batch:
#   - valida versao (>=3.9) e arquitetura (64-bit, exigido pelo pymem)
#   - testa permissao de escrita na pasta
#   - cria um ambiente isolado .venv (reusa se ja existir e for valido)
#   - detecta proxy do Windows (rede corporativa) e repassa ao pip
#   - instala o NUCLEO (customtkinter, pymem, capstone) um a um
#   - instala os EXTRAS da overlay (numpy, pillow, winsdk) - falha nao aborta
#   - verifica de verdade importando cada modulo
#
#  Mensagens em PT-BR sem acentos de proposito (funciona em qualquer code page).
# ============================================================================
import os
import sys
import struct
import shutil
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
VENV = os.path.join(HERE, ".venv")
VENV_PY = os.path.join(VENV, "Scripts", "python.exe")

# Fallbacks caso os requirements-*.txt nao existam
CORE_FALLBACK = ["customtkinter>=5.2.0", "pymem>=1.13.0", "capstone>=5.0.0"]
OPT_FALLBACK = ["numpy>=1.24.0", "pillow>=10.0.0", "winsdk>=1.0.0"]
# nome do import usado na verificacao (pillow importa como PIL)
CORE_IMPORTS = ["customtkinter", "pymem", "capstone"]
OPT_IMPORTS = ["numpy", "PIL", "winsdk"]


def line():
    print("-" * 62)


def read_reqs(fname, fallback):
    path = os.path.join(HERE, fname)
    try:
        items = []
        for ln in open(path, encoding="utf-8"):
            ln = ln.split("#")[0].strip()
            if ln:
                items.append(ln)
        return items or fallback
    except Exception:
        return fallback


def pkg_name(spec):
    for sep in (">=", "<=", "==", ">", "<", "~="):
        spec = spec.split(sep)[0]
    return spec.strip()


def sys_proxy():
    # Le o proxy do WinINET (rede corporativa/escola) e devolve args --proxy p/ pip.
    try:
        import winreg
        k = winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            r"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
        )
        enable, _ = winreg.QueryValueEx(k, "ProxyEnable")
        if enable:
            server, _ = winreg.QueryValueEx(k, "ProxyServer")
            if server and "=" not in server:  # formato simples host:porta
                return ["--proxy", "http://" + server]
    except Exception:
        pass
    return []


def main():
    print("=" * 62)
    print("  tbh_bot - instalando dependencias")
    print("=" * 62)

    v = sys.version_info
    bits = struct.calcsize("P") * 8
    print("Python detectado: %d.%d.%d (%d-bit)" % (v.major, v.minor, v.micro, bits))

    if v < (3, 9) or bits != 64:
        line()
        print("ERRO: e necessario Python 64-bit versao 3.9 ou mais nova.")
        if bits != 64:
            print("  Este Python e 32-bit; o tbh_bot le a memoria de um jogo 64-bit.")
        print("  Instale o Python 3.12 (Windows 64-bit) em python.org e rode de novo.")
        return 1

    # teste de escrita (pasta em Program Files / somente-leitura / dentro de zip temp)
    try:
        t = os.path.join(HERE, ".tbh_wtest.tmp")
        open(t, "w").close()
        os.remove(t)
    except Exception:
        line()
        print("ERRO: sem permissao de escrita nesta pasta:")
        print("  " + HERE)
        print("  Mova a pasta do projeto para um local seu (ex.: Documentos)")
        print("  e NAO rode de dentro do visualizador de ZIP. Extraia primeiro.")
        return 1

    # .venv: reusa se valido, senao recria
    venv_ok = False
    if os.path.exists(VENV_PY):
        venv_ok = subprocess.call(
            [VENV_PY, "-c", "import sys"],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        ) == 0
    if not venv_ok:
        if os.path.isdir(VENV):
            print("Removendo .venv antigo/invalido...")
            try:
                shutil.rmtree(VENV)
            except Exception:
                line()
                print("ERRO: nao consegui apagar o .venv.")
                print("  O painel/TBH.bat esta aberto? Feche e rode o INSTALL.bat de novo.")
                return 1
        print("Criando ambiente isolado (.venv)... aguarde alguns segundos.")
        if subprocess.call([sys.executable, "-m", "venv", VENV]) != 0:
            line()
            print("ERRO: falha ao criar o .venv.")
            return 1
    if not os.path.exists(VENV_PY):
        line()
        print("ERRO: .venv criado mas sem python.exe. Abortando.")
        return 1

    proxy = sys_proxy()
    if proxy:
        print("Proxy do Windows detectado: " + proxy[1])

    base = [
        VENV_PY, "-m", "pip", "install",
        "--no-input", "--disable-pip-version-check", "--timeout", "60",
    ] + proxy

    print("Atualizando o pip...")
    subprocess.call(base + ["--upgrade", "pip"])

    core = read_reqs("requirements-core.txt", CORE_FALLBACK)
    opt = read_reqs("requirements-optional.txt", OPT_FALLBACK)

    def install_one(spec, only_binary=False, pre=False):
        args = list(base)
        if pre:
            args.append("--pre")  # winsdk so existe como pre-release (ex.: 1.0.0b10)
        args.append("--only-binary=:all:" if only_binary else "--prefer-binary")
        args.append(spec)
        return subprocess.call(args) == 0

    line()
    print("Instalando o ESSENCIAL (abre o painel)...")
    line()
    core_fail = []
    for spec in core:
        name = pkg_name(spec)
        print(">> " + name + " ...")
        if not install_one(spec):
            core_fail.append(name)
            print("   FALHOU: " + name)

    line()
    print("Instalando EXTRAS da overlay de preco (opcional)...")
    line()
    for spec in opt:
        name = pkg_name(spec)
        is_winsdk = name.lower() == "winsdk"  # so wheel + pre-release
        print(">> " + name + " (opcional) ...")
        if not install_one(spec, only_binary=is_winsdk, pre=is_winsdk):
            print("   (opcional falhou - o painel funciona, so a overlay fica off)")

    # verificacao real por import (dentro do .venv)
    def can_import(mods):
        return subprocess.call(
            [VENV_PY, "-c", "import " + ", ".join(mods)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        ) == 0

    line()
    print("Verificando a instalacao...")
    core_ok = can_import(CORE_IMPORTS)
    tk_ok = can_import(["tkinter"])
    opt_ok = can_import(OPT_IMPORTS)
    line()
    print("RESULTADO:")
    print(("  [OK]  " if core_ok else "  [X ]  ") +
          "nucleo do painel  (customtkinter, pymem, capstone)")
    print(("  [OK]  " if tk_ok else "  [X ]  ") +
          "tkinter           (interface grafica)")
    print(("  [OK]  " if opt_ok else "  [--]  ") +
          "overlay de preco  (numpy, pillow, winsdk)  - opcional")
    line()

    if core_ok and tk_ok:
        print("PRONTO! Instalacao concluida com sucesso.")
        print("Agora e so DAR DUPLO-CLIQUE em TBH.bat para abrir o painel.")
        return 0

    print("A instalacao do nucleo NAO terminou. Como resolver:")
    if not tk_ok:
        print("  - Falta o tkinter: reinstale o Python marcando 'tcl/tk and IDLE'.")
    if not core_ok:
        alvo = ", ".join(core_fail) if core_fail else "ver mensagens acima"
        print("  - Falhou: " + alvo)
        print("    Verifique internet/proxy e rode o INSTALL.bat de novo.")
        print("    Se seu Python for muito novo (3.13+), instale o Python 3.12 (64-bit).")
    return 1


if __name__ == "__main__":
    try:
        rc = main()
    except KeyboardInterrupt:
        print("\nCancelado pelo usuario.")
        rc = 1
    except Exception as e:
        print("ERRO inesperado: %s" % e)
        rc = 1
    sys.exit(rc)
