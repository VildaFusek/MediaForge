# AGENTS.md — MediaForge

## Projekt a priority
- MediaForge: C# / .NET 10 / WinUI 3 / Windows App SDK / x64 / VS 2026; portable unpackaged (`WindowsPackageType=None`), self-contained. FFmpeg/FFprobe jsou soukromé binárky aplikace.
- MVVM (`CommunityToolkit.Mvvm`); české UI a komunikace, anglické identifikátory. Existující kód, architektura a zvolené UX mají přednost před obecnými vzory.
- Priority: **bezpečnost dat > správnost > kompatibilita > minimální změna > úspora tokenů**. Úspora nesmí vést k přehlédnutí rizika ztráty dat.

## Práce s kontextem — zásadní
1. Ze zadání určete konkrétní funkci a jejího vlastníka (View / ViewModel / Service). Neprovádějte plošný průzkum projektu.
2. Nejprve použijte cílené hledání názvů symbolů/souborů. Čtěte jen nalezené úseky a bezprostřední závislosti; celý soubor jen když je nezbytný.
3. Neprocházejte automaticky všechny složky, všechna XAML, historii Gitu, README ani dokumentaci. Neopakujte již provedené hledání bez důvodu.
4. Před přidáním nového typu/služby vyhledejte existující řešení. U změn veřejných rozhraní dohledávejte relevantní volající, bindingy a registrace.
5. Preferujte malé přesné patche. Neformátujte nesouvisející řádky, nepřepisujte celé soubory, nevytvářejte zbytečné vrstvy, balíčky nebo dokumenty.
6. Při rozsáhlém úkolu postupujte po funkčních celcích. Pokud další krok vyžaduje rozhodnutí s dopadem na data či architekturu, zeptejte se; jinak zvolte konzervativní postup.
7. Nevypisujte mezivýsledky hledání, velké bloky existujícího kódu ani dlouhé úvahy. Výjimkou jsou údaje potřebné k rozhodnutí nebo řešení chyby.

## Hranice odpovědnosti a ověřování
- **Bez výslovného pokynu nikdy nespouštějte build, testy, aplikaci, analyzéry spouštějící kód, FFmpeg, benchmarky ani lokální runtime ověřování.** Nepoužívejte `dotnet build/test`, `msbuild` ani nasazení.
- Povolená je pouze cílená statická kontrola změněných míst a jejich kontraktů: syntaxe, typy a signatury, namespace, XAML bindingy/handlery, zřejmé logické a datové chyby. Není nutné kontrolovat celý projekt.
- Nikdy netvrďte, že změna prošla sestavením či testy. Diagnostiku z VS dodává uživatel; opravte příčinu a přímé dopady, ne nesouvisející varování.
- Odpověď česky, typicky 1–3 stručné věty: **co se změnilo; soubory; případné riziko/ruční ověření**. Bez dlouhého shrnutí, návrhů dalších funkcí a opakovaných instrukcí k buildu.

## Architektura a růst projektu
- `Views/`: XAML a UI události; `ViewModels/`: stav/příkazy; `Models/`: datové typy; `Services/`: I/O a média; `Controls/`, `Themes/`, `Helpers/`: sdílené prvky.
- `MainWindow`: title bar, Mica, `RootFrame`; `MainPage`: explorer. Respektujte stávající rozdělení odpovědností.
- Novou funkcionalitu zařaďte do existujícího modulu; nové soubory vytvářejte jen při jasné nové odpovědnosti. Nevytvářejte „god classes“, duplicitní služby ani abstrakce pro jediné použití.
- Zachovejte stávající public API a uživatelské chování, pokud zadání neurčuje jinak. Při zásahu do sdílených kontraktů ověřte staticky přímé spotřebitele.
- Neupravujte generované `bin/` a `obj/`; nepřidávejte NuGet závislosti bez skutečné potřeby a výslovného souhlasu.

## WinUI / MVVM
- Používejte skutečné WinUI 3 API, ne WPF konstrukce. Kontrolujte `x:Class`, namespace, `DataContext`, `{Binding}` vs. `{x:Bind}` a signatury handlerů.
- CommunityToolkit: `[ObservableProperty]` na `partial` properties (ne field; MVVMTK0045), `[RelayCommand]`; asynchronně `Task`, `async void` jen UI event handlery.
- Blokující I/O mimo UI thread; změny pozorovaného UI stavu na UI thread. Zohledněte cancellation, souběh a opožděné callbacky.
- Zachovejte chování klávesnice, multi-select a kontextové nabídky pro správnou cílovou položku.

## Bezpečnost souborů (nevynechávat ani kvůli úspoře)
- Bez potvrzení nepřepisujte ani trvale nemažte data. Preferujte Koš; nikdy nepřecházejte tiše na permanentní smazání.
- Přesun mezi svazky nesmí automaticky mazat originál bez samostatně prověřené integrity a řešení selhání/souběhu.
- Kopírování: streamování s omezenou pamětí, cancellation, dočasný cíl, bezpečné dokončení; explicitní chování při částečném selhání.
- Ošetřete kolize, složku kopírovanou do sebe, nepřístupné cesty, symlinky/junctions/reparse points a změny během operace.
- SHA-256 neověřuje metadata ani budoucí neměnnost. Neslibujte transakce/rollback bez implementace. Interní schránka není systémová schránka Windows.

## FFmpeg / FFprobe
- Používejte vlastní binárky v zapisovatelném runtime umístění; žádné spoléhání na PATH či systémovou instalaci.
- Stable/nightly: důvěryhodné ověřené zdroje, bezpečné rozbalení, staging, přepnutí a rollback; nenahrazujte spuštěné binárky.
- Spouštění přes `ProcessStartInfo.ArgumentList` bez shellu; korektní logy/progress/cancellation; nikdy neměňte vstupní soubory bez pokynu.
- Hardwarovou akceleraci detekujte, nepředpokládejte NVENC/QSV/AMF. Dodržujte příslušné LGPL/GPL podmínky.

## Rozsah
- Dělejte jen požadovanou změnu a nezbytné související opravy. Odhalené nesouvisející problémy pouze krátce oznamte.
- Nezastavujte jasné zadání zbytečnými otázkami; na nevratné či bezpečnostně významné rozhodnutí se ptejte.
- Build, testování a konečné ověření vždy řídí uživatel.
