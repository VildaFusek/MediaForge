# Příspěvky do MediaForge

Před odesláním větší změny otevřete issue s popisem problému a zamýšleného řešení. Licence vlastního kódu dosud není vybrána; před přispěním si se správcem vyjasněte podmínky přijetí a licencování příspěvku.

## Hlášení chyby

Použijte šablonu issue a uveďte:

- verzi nebo commit MediaForge, verzi Windows a architekturu;
- přesný postup, očekávaný a skutečný výsledek;
- při problému s převodem také verzi FFmpeg, profil, HDR volbu, enkodér a případně GPU / ovladač;
- relevantní anonymizovaný výřez protokolu.

Nezveřejňujte soukromá videa, přístupové údaje, celé databáze ani neupravené protokoly. Pro reprodukci použijte neosobní vzorek, k jehož sdílení máte právo. Při podezření na bezpečnostní zranitelnost nezveřejňujte exploit ani citlivá data; použijte soukromé hlášení v záložce Security, pokud je v repozitáři povolené.

## Změny kódu

- Respektujte [AGENTS.md](AGENTS.md), existující architekturu a české UI; identifikátory pište anglicky.
- Udržujte pull request zaměřený na jeden problém, bez nesouvisejícího formátování.
- Zachovejte rozdělení Views / ViewModels / Services a používejte skutečné WinUI 3 API.
- Nepřidávejte závislosti nebo změny veřejných kontraktů bez zdůvodnění.
- U souborových operací popište chování při kolizi, zrušení, částečném selhání a práci s odkazy. Bez potvrzení nepřepisujte ani trvale nemažte uživatelská data.
- Nevkládejte výstupy sestavení, FFmpeg binárky, soukromá média, lokální databáze ani klíče.

V pull requestu oddělte skutečně provedená ověření od neověřených předpokladů. Pokyny pro automatizované agenty v `AGENTS.md` zakazují spouštět build, testy a aplikaci bez výslovného pokynu; ověřování řídí uživatel.
