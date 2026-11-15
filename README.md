# MediaForge

Desktopová aplikace pro Windows s českým rozhraním pro procházení, analýzu a převod videí. Postavená na C# / .NET 10 a WinUI 3.

## Co aplikace umí

- Procházení složek a disků, seznam nebo miniatury, řazení a filtrování videí.
- Analýzu pomocí FFprobe a filtry podle kodeku, kontejneru, rozlišení, FPS, délky, datového toku, zvuku a titulků.
- Kopírování, volitelné ověření obsahu SHA-256, přejmenování a přesun do Koše.
- Přehrávání pomocí soukromé instalace FFplay.
- Frontu převodů s profily zařízení, výběrem video a zvukové stopy, vlastním cílem a nastavením kvality.
- Detekci hardwaru a ověřování dostupnosti hardwarových enkodérů.
- Samostatný monitor převodu a protokoly jednotlivých úloh.

## Požadavky a spuštění

- Cílová platforma: **Windows x64**. Projekt deklaruje minimum Windows 10 verze 1809; skutečnou kompatibilitu konkrétního vydání je nutné ověřit před distribucí.
- Aplikace je připravována jako portable, unpackaged a self-contained. Distribuujte a rozbalte **celou publikační složku**, nikoli pouze `MediaForge.exe`.
- Umístěte aplikaci do zapisovatelné složky uživatele, ne do chráněné složky typu `Program Files`.
- Pro stažení FFmpeg je potřeba připojení k internetu. Instalaci spouští uživatel potvrzením v aplikaci.

Pokud je v repozitáři publikované vydání, najdete balíček v sekci **Releases**. Tento dokument sám o sobě neznamená, že již existuje ověřené binární vydání.

### První použití

1. Spusťte `MediaForge.exe` z rozbalené složky.
2. Přes indikátor FFmpeg otevřete přípravu nástrojů a potvrďte stažení.
3. Vyberte složku s videi. Pro převod otevřete **Převést videa**, přidejte soubory a vyberte profil.
4. Zkontrolujte cílovou složku a nastavení a spusťte převod.

FFmpeg, FFprobe a FFplay se instalují ze stabilního balíčku Gyan.dev Essentials do `Tools/FFmpeg` vedle aplikace. Aplikace nespoléhá na systémový FFmpeg ani `PATH`. Kontrolní součet pochází ze stejného HTTPS zdroje jako archiv; nejde o nezávislý digitální podpis.

## Omezení a bezpečnost dat

- Výstup převodu je MP4 s jednou video stopou a jednou zvukovou stopou AAC stereo, bez titulků. Nejde o bezeztrátovou archivaci všech stop a metadat.
- Převod ponechává originály a existující výstupy nepřepisuje. Samostatné operace se soubory mohou originály přejmenovat, přesunout nebo odeslat do Koše; důležitá data předem zálohujte.
- SHA-256 ověřuje obsah, nikoli metadata ani budoucí neměnnost souboru.
- Zachování HDR10/HLG vyžaduje odpovídající HEVC nastavení. Dolby Vision a zachování HDR10+ nejsou podporovány. HDR → SDR vyžaduje filtry `zscale` a `tonemap`.
- Dostupnost GPU závisí na hardwaru, ovladačích a konkrétním vstupu. Profily nezaručují kompatibilitu se všemi zařízeními.
- Fronta převodů se po restartu neobnovuje; protokoly zůstávají na disku.
- Přestože řešení obsahuje i konfigurace x86 a ARM64, integrovaná distribuce FFmpeg je určena pro x64.

## Místní data a diagnostika

| Umístění | Obsah |
| --- | --- |
| `Tools/FFmpeg` vedle aplikace | Nástroje, jejich verze, licence a instalační protokoly v podsložce `logs` |
| `%LOCALAPPDATA%/MediaForge` | Místní data včetně databáze diagnostiky |
| `%LOCALAPPDATA%/MediaForge/Logs` | Aplikační protokoly |
| `%LOCALAPPDATA%/MediaForge/ConversionLogs` | Protokoly převodů |

Portable distribuce tedy neznamená, že všechna data zůstávají vedle EXE. Protokoly a databáze mohou obsahovat názvy médií, úplné cesty a technická metadata. Před sdílením je zkontrolujte a anonymizujte.

## Vývoj

Předpoklady: Windows, Visual Studio 2026 s podporou WinUI / Windows App SDK, .NET 10 SDK a Windows SDK pro cílení `10.0.26100.0`.

1. Otevřete `MediaForge.slnx`.
2. Obnovte NuGet balíčky.
3. Vyberte konfiguraci **x64**; pro distribuci použijte **Release** a profil `MediaForge/Properties/PublishProfiles/win-x64.pubxml`.

Publikování vytváří složku s více soubory. Release konfigurace zapíná trimming a ReadyToRun; výsledný balíček je nutné samostatně ručně ověřit. Některé závislosti Windows SDK / Windows App SDK používají plovoucí verze `*`, takže obnova není pevně reprodukovatelná.

### Struktura

- `MediaForge/Views` — WinUI obrazovky a UI události.
- `MediaForge/ViewModels` — stav a příkazy MVVM.
- `MediaForge/Services` — souborové operace, analýza, převody a správa nástrojů.
- `MediaForge/Models` — datové typy.
- `MediaForge/Controls`, `Themes`, `Helpers` — sdílené UI a pomocný kód.

## Příspěvky, chyby a vydání

Postup pro hlášení chyb a změny je v [CONTRIBUTING.md](CONTRIBUTING.md). Podklady a checklist zveřejnění jsou v [docs/PUBLISHING.md](docs/PUBLISHING.md).

## Licence

**Licence vlastního kódu MediaForge zatím není určena.** Zveřejnění zdrojů samo o sobě neuděluje oprávnění k jejich volnému užití, úpravám nebo další distribuci.

Komponenty třetích stran mají vlastní licence; zejména stahovaný FFmpeg Essentials je varianta GPLv3. Přehled a upozornění pro distribuci jsou v [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
