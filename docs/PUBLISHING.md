# Příprava zveřejnění na GitHubu

Tento dokument je pracovní checklist, nikoli potvrzení dokončeného vydání. V rámci přípravy dokumentace nebyl spuštěn build, testy ani aplikace a nic nebylo nahráno na GitHub.

## Údaje repozitáře

- **Název:** `MediaForge`
- **Popis:** `Český správce a převodník videí pro Windows — WinUI 3, .NET 10 a FFmpeg.`
- **Topics:** `windows`, `winui3`, `dotnet`, `csharp`, `ffmpeg`, `video-converter`, `media-manager`, `mvvm`
- **Web:** ponechte prázdný, dokud projekt nemá vlastní web.
- **Jazyk dokumentace a rozhraní:** čeština.

Před zveřejněním doplňte vlastníka repozitáře a zvolte licenci vlastního kódu. Nevkládejte do dokumentace smyšlené URL, odznaky úspěšného sestavení, počty stažení ani neověřená tvrzení o podpoře.

## Před prvním veřejným commitem

- [ ] Zvolit licenci a držitele práv, přidat odpovídající `LICENSE` a aktualizovat README i CONTRIBUTING.
- [ ] Prověřit práva ke všem zdrojům, obrázkům, logům a dalším assetům; vyřešit povinnosti třetích stran.
- [ ] Zkontrolovat skutečný seznam souborů určených ke commitu. `.gitignore` neodstraní soubory, které už Git sleduje, ani tajné údaje v historii.
- [ ] Nezahrnout `.vs`, `bin`, `obj`, `*.user`, databáze, protokoly, FFmpeg instalace, média ani podpisové klíče.
- [ ] Prověřit zdroje a konfigurace na osobní cesty, tokeny, hesla a soukromé adresy; uniklé přístupy případně zneplatnit.
- [ ] Případné screenshoty pořídit s neosobními názvy souborů a přidat je do README až jako skutečně existující soubory.
- [ ] Nastavit GitHub Issues; případně povolit soukromé hlášení zranitelností a uvést ověřený bezpečnostní kontakt.

Repozitář v této pracovní složce při přípravě nebyl inicializován. Inicializace, výběr remote, commit i push zůstávají na vlastníkovi; tento checklist je automaticky neprovádí.

## Před binárním vydáním

- [ ] Vybrat verzi, odpovídající tag a přesný zdrojový commit; uvést stav vydání pravdivě, případně použít pre-release.
- [ ] Zaznamenat verze SDK a skutečně obnovených závislostí; plovoucí verze v projektu omezují reprodukovatelnost.
- [ ] Podle rozhodnutí správce publikovat Release / x64 přes `win-x64.pubxml`.
- [ ] Ověřit skutečný **publikovaný** výstup, nejen Debug: start, XAML bindingy a dopady trimmingu.
- [ ] Vyzkoušet na čistém cílovém Windows bez vývojového prostředí a samostatně nainstalovaného .NET / Windows App SDK; podle výsledku stanovit skutečně podporované verze Windows.
- [ ] Ověřit první stažení FFmpeg, chybu sítě, zrušení a nezapisovatelnou složku.
- [ ] Ověřit analýzu, miniatury, FFplay a převod reprezentativních SDR/HDR vstupů; GPU pouze tam, kde byla skutečně ověřena.
- [ ] Na postradatelných datech ověřit kolize, zachování originálů, zrušení převodu a chování souborových operací při selhání.
- [ ] Zkontrolovat licenční texty v distribučním výstupu včetně Feather a runtime závislostí.
- [ ] Balit celou čistou publikační složku, bez místních dat, protokolů a dodatečně stažené složky `Tools/FFmpeg`. Nemazat kvůli balení pracovní instalaci; připravit samostatnou čistou kopii.
- [ ] Vytvořit ZIP, například `MediaForge-vX.Y.Z-win-x64.zip`, a po dokončení vypočítat jeho SHA-256.
- [ ] Připojit ZIP a kontrolní součet k vydání a uvést skutečný stav digitálního podpisu. SHA-256 není podpis ani důkaz důvěryhodnosti.

## Šablona poznámek k vydání

Před použitím nahraďte všechny zástupné údaje a vynechte tvrzení, která nebyla ověřena.

```markdown
# MediaForge vX.Y.Z

Stav: [preview / stabilní podle skutečného ověření]
Platforma: Windows x64
Ověřeno na: [verze a sestavení Windows]
Zdrojový commit: [hash]

## Změny
- [Skutečně dokončené změny]

## Stažení a spuštění
Rozbalte celý ZIP do zapisovatelné složky a spusťte MediaForge.exe.
FFmpeg se stáhne samostatně po potvrzení v aplikaci; není součástí ZIP.
Část místních dat se ukládá do %LOCALAPPDATA%/MediaForge.

## Známá omezení
- MP4, jedna video a jedna zvuková stopa AAC stereo, bez titulků.
- Fronta převodů se po restartu neobnovuje.
- [Další omezení konkrétního vydání]

## Integrita a podpis
Soubor: MediaForge-vX.Y.Z-win-x64.zip
SHA-256: [skutečný kontrolní součet finálního ZIP]
Digitální podpis: [skutečný stav]
```
