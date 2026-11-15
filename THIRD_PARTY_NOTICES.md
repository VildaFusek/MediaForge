# Komponenty třetích stran

Tento přehled nenahrazuje licenční texty jednotlivých balíčků ani úplný audit konkrétního vydání. Licence MediaForge se na cizí komponenty nevztahuje.

## FFmpeg, FFprobe a FFplay

- Projekt: https://ffmpeg.org/
- Zdroj stahovaného sestavení: https://www.gyan.dev/ffmpeg/builds/
- Používaný balíček: `ffmpeg-release-essentials.zip`, Windows x64.
- Tato varianta je distribuována pod GPLv3; rozhodující jsou licence a konfigurace konkrétního staženého sestavení.

Aplikace nástroje spouští jako samostatné procesy. Jejich instalace obsahuje dokumentaci a licence, které se nesmějí při další distribuci odstranit.

Pokud budete FFmpeg přibalovat do vlastního vydání, předem vyřešte všechny povinnosti odpovídající konkrétnímu sestavení, včetně poskytnutí odpovídajících zdrojových kódů způsobem požadovaným licencí. Samotný odkaz na web FFmpeg ani tato poznámka nemusí tyto povinnosti splnit. Výchozí příprava vydání má proto obsahovat pouze aplikaci bez stažené složky `Tools/FFmpeg`.

## Feather icons

Autor: Cole Bemis. Licence: MIT.

Ikony pocházejí z kolekce Feather přes SVG Repo a jsou upraveny pro nativní WinUI vykreslování. Původ, seznam použitých zdrojů a úplný licenční text jsou v [MediaForge/Assets/Icons/Feather-LICENSE.txt](MediaForge/Assets/Icons/Feather-LICENSE.txt). Projekt tento soubor kopíruje do výstupu i publikace; ve vydání jej zachovejte.

## NuGet a runtime komponenty

Projekt přímo odkazuje na tyto balíčky:

- CommunityToolkit.Mvvm
- CommunityToolkit.WinUI.Controls.Sizers
- Microsoft.Data.Sqlite
- Microsoft.Extensions.DependencyInjection
- Microsoft.Extensions.Hosting
- Microsoft.Extensions.Logging
- Microsoft.Windows.SDK.BuildTools
- Microsoft.Windows.SDK.BuildTools.WinApp
- Microsoft.WindowsAppSDK
- Serilog

Konkrétní verze jsou deklarovány v [MediaForge.csproj](MediaForge/MediaForge.csproj); některé jsou plovoucí. Před vydáním evidujte skutečně obnovené verze a zkontrolujte licence přímých i tranzitivních závislostí a přibalených .NET / Windows App SDK runtime komponent. Tento seznam není úplným soupisem obsahu self-contained balíčku.
