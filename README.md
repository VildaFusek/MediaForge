# MediaForge

Desktopová aplikace pro Windows s českým rozhraním pro procházení, analýzu a převod videí. Postavená na C# / .NET 10 a WinUI 3.

## Co aplikace umí

- Jednotné pracovní prostředí se souborovým správcem a panelem převodů, horní nabídkou **Soubor / Úpravy / Nástroje** a rozložením podle šířky okna.
- Procházení složek a disků, seznam nebo miniatury a řazení videí. Dvojklik přidá video do fronty, dvojklik na složku ji otevře; **Ctrl+A** označí všechny zobrazené položky v souborovém panelu.
- Analýzu pomocí FFprobe a filtry podle kodeku, kontejneru, výšky obrazu, FPS, délky, datového toku, zvuku a titulků. Nabídky technických hodnot se průběžně doplňují podle analyzovaných videí.
- Kopírování, volitelné ověření obsahu SHA-256, přejmenování a přesun do Koše.
- Přehrávání pomocí soukromé instalace FFplay.
- Frontu převodů s profily zařízení, výběrem video a zvukové stopy, vlastním cílem a nastavením kvality.
- Detekci hardwaru a ověřování dostupnosti hardwarových enkodérů.
- Volbu zatížení počítače a rychlosti enkódování nezávisle na profilu zařízení.
- Adaptivní doporučení a automatický výběr CPU/GPU podle krátkého porovnání skutečného vstupu, s místním uložením výsledků pro podobné převody.
- Samostatný monitor převodu a protokoly jednotlivých úloh.

## Požadavky a spuštění

- Cílová platforma: **Windows x64**. Projekt deklaruje minimum Windows 10 verze 1809; skutečnou kompatibilitu konkrétního vydání je nutné ověřit před distribucí.
- Aplikace je připravována jako portable, unpackaged a self-contained. Distribuujte a rozbalte **celou publikační složku**, nikoli pouze `MediaForge.exe`.
- Umístěte aplikaci do zapisovatelné složky uživatele, ne do chráněné složky typu `Program Files`.
- Pro stažení FFmpeg je potřeba připojení k internetu. Instalaci spouští uživatel potvrzením v aplikaci.

Pokud je v repozitáři publikované vydání, najdete balíček v sekci **Releases**. Tento dokument sám o sobě neznamená, že již existuje ověřené binární vydání.

### První použití

1. Spusťte `MediaForge.exe` z rozbalené složky.
2. Přes indikátor FFmpeg nebo nabídku **Nástroje → FFmpeg: instalace a aktualizace…** otevřete přípravu nástrojů a potvrďte stažení.
3. Vyberte složku s videi. Video přidejte dvojklikem; více vybraných videí tlačítkem **Do fronty převodů**. Další soubory lze přidat také přímo v panelu převodů.
4. V panelu **Nastavení a fronta převodů** vyberte profil zařízení, HDR, režim zatížení a rychlost enkódování. Zkontrolujte cílovou složku a případně video či zvukovou stopu.
5. Klikněte na **Spustit převod**. Při zapnutém adaptivním výběru a automatickém enkodéru nebo dekodéru může první převod zahrnovat porovnání CPU/GPU.

Panel převodů je vedle souborů, v užším okně pod nimi. Tlačítkem **Převody** jej lze skrýt nebo znovu zobrazit; skrytí panelu ani zavření samostatného monitoru běžící převod nezastavuje. Samotné přidání do fronty převod nespustí.

FFmpeg, FFprobe a FFplay se instalují ze stabilního balíčku Gyan.dev Essentials do `Tools/FFmpeg` vedle aplikace. Aplikace nespoléhá na systémový FFmpeg ani `PATH`. Kontrolní součet pochází ze stejného HTTPS zdroje jako archiv; nejde o nezávislý digitální podpis.

### Filtry a ovládání souborů

- **Ctrl+A** v souborovém panelu označí všechny zobrazené položky včetně složek; nepřidává skryté nebo odfiltrované soubory. V textových polích slouží k označení textu a v panelu převodů neovládá seznam souborů.
- Rozbalovací nabídky kodeků, kontejnerů, výšky, FPS, délky a datového toku vycházejí z analýzy aktuální složky se zohledněním hledaného názvu a zobrazení skrytých položek. Metadata filtry nabídky vzájemně nezužují, aby ostatní možnosti nezmizely.
- Výška, délka a datový tok mají dolní a horní mez; FPS vybírá konkrétní hodnotu zaokrouhlenou na tři desetinná místa. Nabídky délek používají hranice celých minut, datový tok hranice desetin Mb/s.
- Složky zůstávají při filtrování metadat viditelné. Videa bez potřebných údajů se při aktivním filtru skryjí a mohou přibývat během analýzy.

### Zatížení počítače a rychlost převodu

| Režim zatížení | CPU vlákna pro jednotlivé fáze | Priorita procesu FFmpeg |
| --- | --- | --- |
| Úsporné | Přibližně čtvrtina logických procesorů, nejméně jedno vlákno | Nízká |
| Vyvážené (výchozí) | Přibližně polovina logických procesorů, nejméně jedno vlákno | Snížená |
| Maximální | Počet logických procesorů | Běžná |

Počty vláken nejsou pevným procentuálním limitem celkového zatížení: dekódování, filtry a enkodér mají vlastní pracovní vlákna. GPU nemá stanovený procentuální limit vytížení.

Samostatná volba **Rychlost enkódování** nabízí rychlejší převod, vyváženou rychlost a kvalitu nebo pomalejší převod s důrazem na kvalitu. Nastavuje příslušný preset CPU, NVIDIA NVENC, Intel Quick Sync nebo AMD AMF enkodéru. Rychlejší nastavení může při stejném datovém toku snížit kvalitu obrazu; nezaručuje konkrétní dobu převodu.

Změny nastavení se používají na čekající úlohy, nikoli na právě běžící převod. Lze je použít společně nebo pouze na vybrané čekající video.

### Adaptivní výběr CPU/GPU

Volba **Učit se výkon tohoto PC a automaticky doporučovat CPU / GPU** je výchozí zapnutá. Jde o empirické učení z místních měření, nikoli o neuronovou síť.

- Při automatickém výběru program porovnává dostupné cesty na stejném úseku vstupu a se stejným profilem, režimem zatížení a rychlostí enkódování. Ručně zvolený enkodér nebo dekodér nepřepisuje.
- Každá testovaná cesta má zahřívací a měřený průchod po 48 výstupních snímcích, s celkovým limitem 30 sekund na cestu. Vzorky se neukládají jako video; měření může krátce zatížit i CPU a prodloužit přípravu prvního převodu.
- Porovnává se čas zpracování a spotřebovaný CPU čas procesu FFmpeg. GPU dostane přednost, pokud je alespoň o 15 % rychlejší než CPU, nebo při době zpracování nejvýše o 10 % delší spotřebuje alespoň o 25 % méně CPU času.
- Bez úspěšného CPU referenčního měření může program vybrat úspěšnou GPU cestu, ale neoznačuje ji za prokazatelně efektivnější než CPU. Nedostatečně dlouhý vzorek se jako doporučení neukládá.
- Doporučení se ukládají místně až na sedm dní, nejvýše pro 100 kombinací. Rozlišují hardware a ovladače, verzi FFmpeg, technické vlastnosti vstupu a nastavení převodu. Změna těchto údajů vyvolá nové porovnání; při chybě převodu se související uložené doporučení zneplatní.
- Měření je orientační: nehodnotí spotřebu elektřiny ani obrazovou kvalitu a negarantuje stejný výsledek na celém videu. Filtry zůstávají na CPU; GPU dekódování může vyžadovat přenos snímků zpět do operační paměti.
- Při vypnutém učení automatický enkodér preferuje ověřenou GPU, jinak CPU; automatický dekodér používá CPU. Běžící převod se při chybě GPU skrytě nepřepíná na CPU.

## Omezení a bezpečnost dat

- Výstup převodu je MP4 s jednou video stopou a nejvýše jednou zvukovou stopou AAC stereo, bez titulků. Zvuk lze vynechat. Nejde o bezeztrátovou archivaci všech stop a metadat.
- Převod ponechává originály a existující výstupy nepřepisuje. Samostatné operace se soubory mohou originály přejmenovat, přesunout nebo odeslat do Koše; důležitá data předem zálohujte.
- SHA-256 ověřuje obsah, nikoli metadata ani budoucí neměnnost souboru.
- Zachování HDR10/HLG vyžaduje HEVC a CPU enkodér `libx265`; adaptivní výběr toto omezení neobchází. Dolby Vision profily 7 a 8.1 lze převést do SDR nebo HDR10 pouze z HDR10 kompatibilní základní vrstvy; dynamická metadata a rozšiřující vrstva včetně FEL se zahodí. Profil 5, ostatní profily Dolby Vision a zachování HDR10+ nejsou podporovány. HDR → SDR vyžaduje filtry `zscale` a `tonemap`; zpracování Dolby Vision navíc příslušnou podporu filtru `sidedata`.
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
| `%LOCALAPPDATA%/MediaForge/hardware-learning.json` | Místní měření rychlosti a CPU času a doporučení CPU/GPU |

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
